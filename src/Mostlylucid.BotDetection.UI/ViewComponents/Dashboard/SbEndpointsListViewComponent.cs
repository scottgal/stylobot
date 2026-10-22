using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Dashboard.Data.Queries;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Helpers;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

[DashboardWidget("endpoints", DatasetKind.EndpointStats)]
public class SbEndpointsListViewComponent(
    DashboardAggregateCache aggregateCache,
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string sort = "total",
        string dir = "desc",
        int page = 1,
        int pageSize = 25,
        bool excludeStatic = false,
        bool compact = false,
        // When true, restrict to UPSTREAM CONTENT PAGES only: rows the upstream
        // app serves (not StyloBot's own dashboard / admin / API surface), that
        // are not API endpoints and not static assets. Powers the traffic
        // overview's "Top content pages" widget — API + StyloBot rows don't
        // belong there.
        bool contentOnly = false,
        // Card heading. Defaults to "Endpoints"; the content-pages widget passes
        // its own label.
        string heading = "Endpoints",
        string? audience = null,
        string? range = null,
        // Si1 (dashboard IA collapse plan): URL-bound filters surfaced by the
        // new /dashboard/site landing page. Null/empty leaves the unfiltered
        // result for the legacy `partials/endpoints` and `Activity` callers,
        // so adding these parameters doesn't perturb the existing surfaces.
        string? path = null,
        string? method = null,
        string? threat = null,
        string? botPressure = null,
        // Si2 (endpoint-IA unification): MODE filter -- see EndpointClassifier.ClassifyMode.
        string? mode = null,
        // Si2: response status-code bucket filter (2xx/3xx/4xx/5xx/all), mirroring
        // the filter ServeEndpointsPartialAsync already applies for the interactive
        // hx-get path -- added here too so the VC's own initial-render / non-hx-get
        // callers (e.g. a direct /dashboard/site?status=5xx page load) filter
        // identically instead of only working after the first chip click.
        string? status = null)
    {
        // "custom" is not a duration token: parsing it alone yields (null, null), which
        // silently skips the windowed store branch. One resolver keeps this component,
        // the summary/top-bots/country/user-agent widgets and the detail builder on
        // the same canonical window. See DashboardRangeScope.
        var (startTime, endTime) = DashboardRangeScope.Resolve(HttpContext, range);

        // If the page composer stashed a DashboardPageResult, read the Endpoints slice from
        // it directly (zero store calls). Otherwise fall through to the existing cache-first /
        // parameter-driven self-fetch so VCs rendered on non-composer pages still work.
        //
        // The audience/window check runs FIRST: the composed bundle is built from a fixed,
        // page-level audienceFilter (see SbWidgetBatchMiddleware.BuildBatchWindow, which only
        // ever composes "bots"/"humans"/null — never "all_incl_internal"), so it can silently
        // disagree with a per-request override like the endpoint control's audience chips
        // (including the "Show self-probe" -> "all_incl_internal" toggle). A non-empty
        // audience or an explicit window means the caller wants a SPECIFIC slice, so it must
        // always win over the composed snapshot, never be shadowed by it.
        var pageResult = HttpContext?.Items["sb.dashboard.pageresult"] as DashboardPageResult;

        // The page-level internal toggle is a request input, not a VC parameter: the
        // traffic tag helper never forwards an audience, so ?internal=show must be read
        // here or a retained-window read is mislabelled Lifetime.
        var requestAudience = !string.IsNullOrEmpty(audience)
            ? audience
            : string.Equals(HttpContext?.Request.Query["internal"].ToString(), "show", StringComparison.OrdinalIgnoreCase)
                ? "all_incl_internal"
                : "all";

        // Scope honesty. The bundle's scope describes the bundle's rows, so it may be
        // used ONLY when those rows are what we returned; a parameter-driven read that
        // bypasses the bundle must resolve from the actual inputs. A legacy bundle
        // exposes only the windowed flag, and an explicit false means "not windowed"
        // (do not fall through to the store capability).
        var internalRequested = string.Equals(requestAudience, "all_incl_internal", StringComparison.OrdinalIgnoreCase)
                                || string.Equals(requestAudience, "internal", StringComparison.OrdinalIgnoreCase);

        string? ScopeFromBundle(DashboardPageResult? page)
        {
            if (!string.IsNullOrEmpty(page?.EndpointStatsScope)) return page!.EndpointStatsScope;
            // Internal requested but the producer cannot supply internal endpoint volume:
            // the rows are external, so the label must say so rather than imply internal.
            if (internalRequested && page?.EndpointStatsInternalSupported is not true)
                return EndpointStatsScope.InternalUnavailable;
            return page?.EndpointStatsWindowed is bool windowed
                ? (windowed ? EndpointStatsScope.Windowed : EndpointStatsScope.Lifetime)
                : null;
        }

        string ResolveFromRequest() =>
            EndpointStatsScope.Resolve(
                eventStore.SupportsWindowedEndpointStats,
                eventStore.SupportsInternalEndpointStats,
                requestAudience, startTime, endTime);

        // Set per branch below; never inferred from the bundle when the rows did not come from it.
        string? resolvedScope = null;

        // Dashboard-wide domain scope (DI seam). FOSS default returns null (no filter =
        // today's behavior); a commercial impl supplies the operator's selected domains,
        // threaded into the self-fetch store reads below. The composed path is already
        // scoped upstream via the page window (BuildVisitorsPageWindow).
        var scopedDomains = HttpContext?.RequestServices?
            .GetService<IDashboardDomainScope>()
            ?.GetSelectedDomains(HttpContext);

        // Set when the legacy fallback below hits a cold SWR placeholder (a store-layer
        // decorator like the commercial StaleWhileRevalidatingDashboardEventStore stamps
        // DashboardWarmingSignal instead of blocking the request) -- distinguishes "still
        // warming" from "genuinely zero endpoints" so the view renders a spinner instead
        // of the bare empty state. Same pattern SiteHealthViewComponent already uses.
        var isWarming = false;

        IReadOnlyList<DashboardEndpointStats> data;
        // A page-level internal read is a distinct data request even with no audience
        // parameter and no window: it must take this branch, not the composed/default one.
        if ((!string.IsNullOrEmpty(requestAudience) && !string.Equals(requestAudience, "all", StringComparison.Ordinal))
            || startTime.HasValue)
        {
            // Parameter-driven: always bypass cache/compose, query store with the provided args.
            // Must win over the warming/composed checks below -- a specific audience/window
            // request (including the "Show self-probe" -> "all_incl_internal" toggle) is asking
            // for a slice the composed snapshot can never satisfy, warming or not.
            //
            // THIS is the branch the live page actually takes on first render: the
            // <sb-endpoints-list> tag helper always forwards range="@Model.Filters.Window",
            // so startTime.HasValue is true here on every real page load -- the "legacy
            // fallback" branch below (where the warming-signal check first landed) is
            // effectively dead for that call shape. A cold SWR placeholder from a store-layer
            // decorator returns an empty list immediately here too, so it needs the exact
            // same warming check, or a windowed cold-miss renders as bare "no data" same as
            // the unfiltered one did before the fix.
            // A host can opt into an authoritative, bounded first-paint read for the
            // ordinary range-driven SSR path. This is intentionally a capability rather
            // than a changed IDashboardEventStore contract: a commercial SWR decorator
            // may correctly return a cold placeholder for interactive reads, while the
            // first HTML response must carry real rows. Audience-specific requests retain
            // the normal store path because they are interactive filter slices, not the
            // initial page render.
            // The seeded first-paint slice is the DEFAULT audience: use it only when this
            // read is the default audience too, never for an internal/other slice.
            var firstPaintReader = string.Equals(requestAudience, "all", StringComparison.Ordinal) && startTime.HasValue
                ? DashboardEndpointsFirstPaintContext.Get(HttpContext)
                : null;
            var usedFirstPaint = firstPaintReader is not null;
            data = usedFirstPaint
                ? await firstPaintReader.GetEndpointStatsAsync(
                    500, startTime, endTime, requestAudience, scopedDomains, HttpContext?.RequestAborted ?? default)
                : await eventStore.GetEndpointStatsAsync(500, startTime, endTime, requestAudience, scopedDomains);
            // ALWAYS check the warming signal on an empty result (the 2026-08-13
            // windowed-partial P0): the old guard only checked on the store path, on the
            // theory that "a present reader means real data". On a cold windowed partial
            // swap the reader can be absent OR seeded from a cold compose — an empty
            // reader result then suppressed the warming check and rendered the bare
            // "No endpoint analytics yet" (absence of knowledge encoded as knowledge of
            // absence — the exact bug class the shellWarming seeding fix already called
            // out). A stamped warming signal now always renders the spinner; a genuinely
            // empty compose (no stamp) still renders the honest empty state.
            // Resolve AFTER the read: a remote store learns its windowed/internal
            // capabilities from the response it just fetched, so sampling earlier would
            // label a first direct/internal read with the conservative pre-read defaults.
            // First paint is served from the composed bundle and carries its own
            // capability/scope; only a real store read relies on the refreshed store.
            resolvedScope = usedFirstPaint
                ? (ScopeFromBundle(pageResult) ?? ResolveFromRequest())
                : ResolveFromRequest();
            if (data.Count == 0)
            {
                var windowedDomainTag = scopedDomains is { Count: 1 } ? scopedDomains[0] : null;
                isWarming = DashboardWarmingSignal.IsWarming(HttpContext, "endpoints", windowedDomainTag);
            }
        }
        else if (pageResult is { IsWarming: true })
        {
            // A genuine cold miss -- render the warming placeholder instead of falling through
            // to a live store call. See SbTopBotsViewComponent for the same guard's rationale.
            var warmingScope = EndpointStatsScope.Normalize(ScopeFromBundle(pageResult) ?? ResolveFromRequest());
            ViewData["EndpointsScope"] = warmingScope;
            ViewData["EndpointsLifetimeOnly"] = string.Equals(warmingScope, EndpointStatsScope.Lifetime, StringComparison.Ordinal);
            return View(new EndpointsListModel
            {
                Endpoints = [],
                BasePath = options.Value.BasePath.TrimEnd('/'),
                NavBasePath = string.IsNullOrEmpty(options.Value.NavBasePath) ? null : options.Value.NavBasePath.TrimEnd('/'),
                SortField = sort, SortDir = dir, Page = page, PageSize = pageSize, TotalCount = 0,
                Heading = heading, ContentOnly = contentOnly, IsCompact = compact,
                IsWarming = true,
            });
        }
        else if (pageResult?.Endpoints is { } composedEndpoints)
        {
            data = composedEndpoints;
            resolvedScope = ScopeFromBundle(pageResult) ?? ResolveFromRequest();
        }
        else
        {
            // Legacy: cache-first / store-fallback so the live dashboard hot path is unchanged.
            // The aggregate snapshot is UNSCOPED: serving it for a request that carries a
            // domain scope (commercial selector / ?domain=) would answer a scoped read with
            // all-domain rows. Any non-empty scope must go to the store, which applies the
            // domains filter -- same rule the API/timeseries path enforces remotely.
            var cached = aggregateCache.Current.Endpoints;
            if (cached.Count > 0 && scopedDomains is not { Count: > 0 })
            {
                data = cached;
            }
            else
            {
                data = await eventStore.GetEndpointStatsAsync(options.Value.EndpointsSnapshotCount, domains: scopedDomains);
                // A cold SWR placeholder from a store-layer decorator returns an empty list
                // immediately (never blocks the request) and stamps this signal -- without
                // this check an empty placeholder renders identically to a genuinely-empty
                // domain ("No endpoint analytics yet"), which is the "endpoints always starts
                // empty" bug: siblings like TopBots block-and-return-real on the same cold
                // request, so they never hit this ambiguity.
                var domainTag = scopedDomains is { Count: 1 } ? scopedDomains[0] : null;
                isWarming = DashboardWarmingSignal.IsWarming(HttpContext, "endpoints", domainTag);
            }
            // Resolve AFTER the legacy read for the same capability-freshness reason.
            resolvedScope = ResolveFromRequest();
        }
        // Read AFTER the read above: a remote adapter learns windowed/lifetime from the
        // response it just fetched and starts conservative, so sampling earlier would
        // mislabel a windowed gateway as "lifetime totals" on first paint. The bundle
        // scope wins; the request-scoped fallback covers direct reads. Read back by
        // _Lean.cshtml / Default.cshtml; retained-window spreads via ?retained=1.
        resolvedScope = EndpointStatsScope.Normalize(resolvedScope ?? ResolveFromRequest());
        ViewData["EndpointsScope"] = resolvedScope;
        ViewData["EndpointsLifetimeOnly"] = string.Equals(resolvedScope, EndpointStatsScope.Lifetime, StringComparison.Ordinal);
        // Canonical window token for the table's own links: the range resolved by the
        // page (cookie or query) is not always present in the request query, and a
        // sort/page/search swap without it would ask for a different slice.
        var windowForLinks = range;
        if (string.IsNullOrWhiteSpace(windowForLinks) && startTime is { } s0 && endTime is { } e0)
        {
            windowForLinks = DashboardRoutingHelpers.WindowTokenForMinutes((int)(e0 - s0).TotalMinutes);
        }
        if (!string.IsNullOrWhiteSpace(windowForLinks)) ViewData["EndpointsWindow"] = windowForLinks;

        var basePath = options.Value.BasePath.TrimEnd('/');
        var navBasePath = string.IsNullOrEmpty(options.Value.NavBasePath)
            ? null
            : options.Value.NavBasePath.TrimEnd('/');
        if (contentOnly)
        {
            // "Content pages" = human page views: GET/HEAD, upstream-served, not
            // API/versioned/gRPC, not static assets, not StyloBot's own paths (the
            // /dashboard + /stylobot + /_stylobot mounts). Supersedes excludeStatic.
            data = data.Where(e => EndpointClassifier.IsContentPageRequest(e.Method, e.Path, basePath, navBasePath)).ToList();
        }
        else if (excludeStatic)
        {
            data = data.Where(e => !EndpointClassifier.IsStaticResource(e.Path)).ToList();
        }

        // Si1 filters: path/method/threat/bot_pressure. Applied AFTER the source
        // pull so the same cache-fed snapshot powers every variant of this VC.
        if (!string.IsNullOrEmpty(path))
        {
            data = data.Where(e => e.Path.Contains(path, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrEmpty(method))
        {
            data = data.Where(e => string.Equals(e.Method, method, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrEmpty(mode))
        {
            data = data.Where(e => string.Equals(
                EndpointClassifier.ModeToken(EndpointClassifier.ClassifyMode(e.Method, e.Path)),
                mode, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (status is "2xx" or "3xx" or "4xx" or "5xx")
        {
            data = data.Where(e => string.Equals(e.DominantStatusBucket, status, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        if (!string.IsNullOrEmpty(threat))
        {
            // "Medium+" -> rank >= Medium. Bare "Medium" treated the same -- the
            // operator picked a floor, so we don't gate equality vs. floor on a
            // trailing glyph that's purely cosmetic.
            var min = threat.TrimEnd('+');
            var minRank = ThreatBandRank(min);
            data = data.Where(e => ThreatBandRank(DeriveThreatBand(e.AvgThreatScore)) >= minRank).ToList();
        }
        if (!string.IsNullOrEmpty(botPressure))
        {
            // Match the visual buckets the _RiskBar primitive paints so the
            // chip lines up with what the operator already sees in the column.
            var threshold = botPressure.ToLowerInvariant() switch
            {
                "high" => 0.5,
                "medium" or "med" => 0.2,
                "low" => 0.0001,
                _ => 0.0
            };
            if (threshold > 0)
            {
                data = data.Where(e => e.BotRate >= threshold).ToList();
            }
        }

        IEnumerable<DashboardEndpointStats> sorted = sort switch
        {
            "bots" => dir == "asc" ? data.OrderBy(x => x.BotCount) : data.OrderByDescending(x => x.BotCount),
            "humans" => dir == "asc" ? data.OrderBy(x => x.HumanCount) : data.OrderByDescending(x => x.HumanCount),
            "botrate" => dir == "asc" ? data.OrderBy(x => x.BotRate) : data.OrderByDescending(x => x.BotRate),
            "latency" => dir == "asc" ? data.OrderBy(x => x.AvgProcessingTimeMs) : data.OrderByDescending(x => x.AvgProcessingTimeMs),
            "unique" => dir == "asc" ? data.OrderBy(x => x.UniqueSignatures) : data.OrderByDescending(x => x.UniqueSignatures),
            "bytes" => dir == "asc" ? data.OrderBy(x => x.BytesOut) : data.OrderByDescending(x => x.BytesOut),
            _ => dir == "asc" ? data.OrderBy(x => x.TotalCount) : data.OrderByDescending(x => x.TotalCount)
        };
        return View(new EndpointsListModel
        {
            Endpoints = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            SortField = sort,
            SortDir = dir,
            Page = page,
            PageSize = pageSize,
            TotalCount = data.Count,
            BasePath = basePath,
            NavBasePath = navBasePath,
            Heading = heading,
            ContentOnly = contentOnly,
            IsCompact = compact,
            AllowEndpointPinning = options.Value.EnableEndpointPinning,
            AudienceFilter = requestAudience,
            StatusFilter = string.IsNullOrEmpty(status) ? "all" : status,
            PathFilter = path ?? string.Empty,
            MethodFilter = string.IsNullOrEmpty(method) ? null : method,
            ModeFilter = string.IsNullOrEmpty(mode) ? null : mode,
            ThreatFilter = string.IsNullOrEmpty(threat) ? null : threat,
            BotPressureFilter = string.IsNullOrEmpty(botPressure) ? null : botPressure,
            IsWarming = isWarming,
        });
    }

    // Same threshold ladder the SbEndpointsList view uses to paint the threat
    // shield -- centralising it here keeps the filter behaviour in lockstep
    // with the icon the operator sees.
    private static string DeriveThreatBand(double avgThreatScore) => avgThreatScore switch
    {
        >= 0.80 => "Critical",
        >= 0.60 => "High",
        >= 0.40 => "Medium",
        >= 0.20 => "Low",
        _ => "None",
    };

    private static int ThreatBandRank(string? band) => band switch
    {
        "Critical" => 4,
        "High" => 3,
        "Medium" => 2,
        "Low" => 1,
        _ => 0,
    };
}
