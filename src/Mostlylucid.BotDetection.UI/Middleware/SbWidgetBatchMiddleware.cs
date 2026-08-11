using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Handles the batch widget update route:
///     <c>GET {basePath}/partials/update?widgets=w1,w2&amp;w1.page=2&amp;w1.filter=bots</c>
///     <para>
///     Renders each requested widget to HTML using <see cref="RazorViewRenderer"/>,
///     injects <c>hx-swap-oob="true"</c> on the root element, caches rendered chunks
///     for 2 seconds by (widgetId, sorted params), and streams all chunks as
///     <c>text/html</c> so HTMX can swap each island in place.
///     </para>
///     <para>
///     This middleware is independent of the full dashboard (<c>/_stylobot</c>).
///     Register it with <see cref="AddStyloBotWidgets"/> and
///     <c>app.UseMiddleware&lt;SbWidgetBatchMiddleware&gt;()</c>.
///     </para>
/// </summary>
public sealed class SbWidgetBatchMiddleware
{
    private readonly RequestDelegate _next;
    private readonly StyloBotDashboardOptions _options;
    private readonly RazorViewRenderer _razorViewRenderer;
    private readonly IDashboardEventStore _eventStore;
    private readonly DashboardAggregateCache _aggregateCache;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly LiquidWidgetRenderer _liquidRenderer;
    private readonly DashboardWidgetShingleCache _shingleCache;
    private readonly ILogger<SbWidgetBatchMiddleware> _logger;

    // Default time window used when no window param is supplied on the update request.
    // Matches the TrafficController default (6h) so batch-updated widgets use the same
    // data window as the page they live on.
    private const int DefaultBatchWindowMinutes = 6 * 60;

    public SbWidgetBatchMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        RazorViewRenderer razorViewRenderer,
        IDashboardEventStore eventStore,
        DashboardAggregateCache aggregateCache,
        SignatureAggregateCache signatureCache,
        LiquidWidgetRenderer liquidRenderer,
        DashboardWidgetShingleCache shingleCache,
        ILogger<SbWidgetBatchMiddleware> logger)
    {
        _next = next;
        _options = options;
        _razorViewRenderer = razorViewRenderer;
        _eventStore = eventStore;
        _aggregateCache = aggregateCache;
        _signatureCache = signatureCache;
        _liquidRenderer = liquidRenderer;
        _shingleCache = shingleCache;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        var basePath = _options.BasePath.TrimEnd('/');

        // Handle: POST {basePath}/partials/render — Liquid template rendering for Node SDK
        if (context.Request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase)
            && path.Equals($"{basePath}/partials/render", StringComparison.OrdinalIgnoreCase))
        {
            await HandleLiquidRenderAsync(context);
            return;
        }

        // Only handle: GET {basePath}/partials/update
        if (!context.Request.Method.Equals("GET", StringComparison.OrdinalIgnoreCase)
            || !path.Equals($"{basePath}/partials/update", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        var widgetList = context.Request.Query["widgets"].FirstOrDefault() ?? "summary";
        var widgets = widgetList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var html = await RenderBatchAsync(context, widgets, context.RequestAborted);
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html);
    }

    /// <summary>
    ///     The batch render core, shared by the live <c>/partials/update</c> request path
    ///     and the boot-time L2 shingle pre-render (see <see cref="PrewarmShinglesAsync"/>):
    ///     per-widget shingle fingerprint (widget + params + surface version) → warm/LFU
    ///     split → one compose-on-delta for the misses (or the warm page bundle) → render
    ///     each widget, tag it OOB and store the shingle. Returns the concatenated OOB
    ///     HTML.
    /// </summary>
    internal async Task<string> RenderBatchAsync(HttpContext context, string[] widgets, CancellationToken ct)
    {
        // Signal-Shingle: compute each widget's shingle fingerprint (widget + filter/params
        // + its surface's data-change version) and split the batch into WARM shingles (served
        // from the LFU as-is) and MISSES. A read boosts the fingerprint's LFU score, so the
        // widgets operators actually watch stay resident. A fully-warm delta composes nothing
        // and renders nothing -- it just streams the resident OOB elements.
        var cursor = context.RequestServices.GetService<IDashboardChangeCursor>();
        var fingerprints = new string[widgets.Length];
        var misses = new List<string>(widgets.Length);
        for (var i = 0; i < widgets.Length; i++)
        {
            var q = WidgetRenderHelpers.ExtractWidgetParams(context, widgets[i]);
            var version = cursor?.TickFor(WidgetRenderHelpers.WidgetSurface(widgets[i])) ?? 0L;
            fingerprints[i] = WidgetRenderHelpers.ComputeWidgetShingleFingerprint(widgets[i], q, version);
            if (!_shingleCache.TryGet(fingerprints[i], out _))
                misses.Add(widgets[i]);
        }

        // Compose-on-delta ONLY for the missing widgets: issue ONE batched read for the
        // catalog-covered misses instead of N self-fetches, stashed in HttpContext.Items for
        // the per-widget render helpers. Skipped entirely when everything is already warm.
        if (misses.Count > 0)
            await TryComposeAndStashAsync(context, misses.ToArray(), ct);

        var sb = new StringBuilder();
        for (var i = 0; i < widgets.Length; i++)
        {
            var html = await RenderWidgetWithShingleAsync(context, widgets[i], fingerprints[i]);
            if (!string.IsNullOrEmpty(html))
                sb.Append(html);
        }

        return sb.ToString();
    }

    /// <summary>
    ///     L2 shingle pre-render entry for the boot-time gate (dashboard host, off the
    ///     request path): renders <paramref name="widgetIds"/> through the SAME pipeline
    ///     as a <c>/partials/update</c> request against a synthetic context, so the L2
    ///     shingle cache is fully populated for the default window/domain BEFORE the first
    ///     request is served (the "page must not serve until L2 is populated" gate).
    ///     <paramref name="query"/> carries the envelope params (e.g. window) — the
    ///     per-widget prefixed params follow the SSR's data-sb-params convention when
    ///     filters are in play; the default unfiltered view needs only the bare window.
    ///     Failures are fault-observed and logged — the gate's bounded wait is the only
    ///     consumer, so a broken render must never fail host boot.
    /// </summary>
    public static async Task<bool> PrewarmShinglesAsync(
        IServiceProvider services, string[] widgetIds, IQueryCollection query)
    {
        try
        {
            var middleware = new SbWidgetBatchMiddleware(
                next: null!,
                options: services.GetRequiredService<StyloBotDashboardOptions>(),
                razorViewRenderer: services.GetRequiredService<RazorViewRenderer>(),
                eventStore: services.GetRequiredService<IDashboardEventStore>(),
                aggregateCache: services.GetRequiredService<DashboardAggregateCache>(),
                signatureCache: services.GetRequiredService<SignatureAggregateCache>(),
                liquidRenderer: services.GetRequiredService<LiquidWidgetRenderer>(),
                shingleCache: services.GetRequiredService<DashboardWidgetShingleCache>(),
                logger: services.GetRequiredService<ILogger<SbWidgetBatchMiddleware>>());

            var context = new DefaultHttpContext { RequestServices = services };
            context.Request.QueryString = QueryString.Create(query);

            var html = await middleware.RenderBatchAsync(context, widgetIds, CancellationToken.None);
            return html.Length > 0;
        }
        catch (Exception ex)
        {
            var logger = services.GetService<ILogger<SbWidgetBatchMiddleware>>();
            logger?.LogWarning(ex, "SbWidgetBatch: L2 shingle pre-render failed for {Widgets}", string.Join(',', widgetIds));
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // Compose-on-delta: resolve covered widgets, compose once, stash result
    // -------------------------------------------------------------------------

    /// <summary>
    ///     For the subset of <paramref name="widgets"/> that are catalog-covered,
    ///     resolves the required <see cref="DatasetKind"/>s, builds a single
    ///     <see cref="DashboardPageManifest"/>, calls
    ///     <see cref="IDashboardPageComposer.ComposeAsync"/> exactly once, and
    ///     stashes the <see cref="DashboardPageResult"/> in
    ///     <c>HttpContext.Items["sb.dashboard.pageresult"]</c>.
    ///
    ///     Non-covered widgets (visitors, sessions, threats, useragents) are
    ///     unaffected — their render helpers continue to self-fetch.
    /// </summary>
    // Widget instance key -> DatasetKind. Mirrors the RenderWidgetAsync dispatch so
    // compose-on-delta covers the SAME widget keys the views actually emit (the <sb-*>
    // tag-helper instance ids like "overview-topbots"), NOT the VC-attribute keys. The
    // VC-attribute DashboardWidgetCatalog only covers <vc:> ViewComponents; the traffic
    // page's widgets are tag-helper widgets, so we key compose-on-delta off this map.
    // Widgets with no entry (visitors, sessions, useragents, threats) aren't
    // composer-covered and fall back to their existing self-fetch.
    private static readonly Dictionary<string, DatasetKind> WidgetDatasetKinds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["summary"] = DatasetKind.SummaryStats,
            ["time-chart"] = DatasetKind.TimeBuckets,
            ["countries"] = DatasetKind.GeoBreakdown,
            ["endpoints"] = DatasetKind.EndpointStats,
            ["topbots"] = DatasetKind.BotAggregate,
            ["top-bots"] = DatasetKind.BotAggregate,
            ["top-visitors"] = DatasetKind.BotAggregate,
            ["live-visitors"] = DatasetKind.BotAggregate,
            ["live-activity"] = DatasetKind.BotAggregate,
            ["overview-topbots"] = DatasetKind.BotAggregate,
            // The four Traffic side panels as ONE batch widget (see RenderTrafficPanelsAsync):
            // needs the whole traffic bundle, and the warm-bundle path
            // (TryStashWarmPageBundleAsync) supplies every slice; the kind entry exists so
            // the compose-on-delta path doesn't skip it entirely when no warm bundle exists
            // (BotAggregate is the heaviest slice it renders).
            ["traffic-panels"] = DatasetKind.BotAggregate,
        };

    private async Task TryComposeAndStashAsync(
        HttpContext context,
        string[] requestedWidgets,
        CancellationToken ct)
    {
        // Map requested widget instance keys -> DatasetKinds via the middleware's own
        // dispatch map (the runtime key namespace). Only the covered kinds are fetched;
        // uncovered widgets self-fetch as before.
        var kinds = requestedWidgets
            .Select(k => WidgetDatasetKinds.TryGetValue(k, out var dk) ? (DatasetKind?)dk : null)
            .Where(dk => dk is not null)
            .Select(dk => dk!.Value)
            .Distinct()
            .ToArray();

        if (kinds.Length == 0)
            return; // nothing composer-covered in this batch

        var window = BuildBatchWindow(context);

        // Prefer the warm page bundle the tick materializer keeps fresh out-of-request:
        // a delta update then reads a ready bundle instead of composing on the request
        // thread. Optional (resolved from request services so a widgets-only host with
        // no dashboard cache still works); falls back to the subset compose below.
        var contentCache = context.RequestServices.GetService<IDashboardContentCache>();
        var manifests = context.RequestServices.GetService<IDashboardPageManifestSource>();
        if (await TryStashWarmPageBundleAsync(contentCache, manifests, context, window, _logger, ct))
            return;

        try
        {
            var datasets = kinds
                .Select(k => new DatasetRequest(k, window.TopN, window.BucketMinutes))
                .ToList();
            var request = new DashboardBatchRequest(
                window.StartTime, window.EndTime, datasets,
                window.AudienceFilter, window.ProbMin, window.Domains);
            // One batched read (single-scan on Postgres, fan-out elsewhere), stashed so
            // the per-widget render helpers read their slice instead of self-fetching.
            var bundle = await _eventStore.ComposeBatchAsync(request, ct);

            // Never stash a compose-FAILURE bundle as authoritative: RemoteDashboardEventStore.
            // ComposeBatchAsync degrades to an ALL-NULL DashboardDatasetBundle on any transport
            // or non-success response, and the per-widget renderers treat a present stash as
            // "real data, no self-fetch needed" — stashing it would render EMPTY shingles and
            // cache them as authoritative (the prod P0: a gateway down at site boot made the
            // L2 pre-render latch empty panels that stayed zero until a surface-version bump).
            // Same completeness shape the SSR path already enforces
            // (StyloBotDashboardMiddleware.IsPageBundleCompleteEnoughToStash): a genuinely
            // empty window still returns non-null EMPTY lists, so null slices mean the compose
            // did not deliver — leave Items unset and let each widget's self-fetch fallback
            // hit the live store instead.
            if (!HasAnyRequestedData(bundle, kinds))
            {
                _logger.LogDebug(
                    "SbWidgetBatch: compose returned no data for the requested kinds — falling back to per-widget self-fetch");
                return;
            }

            context.Items["sb.dashboard.pageresult"] = new DashboardPageResult(bundle);
        }
        catch (Exception ex)
        {
            // Compose failure is non-fatal: widgets fall back to their self-fetch.
            _logger.LogDebug(ex, "SbWidgetBatch: compose failed — falling back to per-widget self-fetch");
        }
    }

    /// <summary>
    ///     True when the composed bundle actually carries a slice for at least one of the
    ///     requested <see cref="DatasetKind"/>s. Null (not empty-list) is the compose-failure
    ///     sentinel — <see cref="RemoteDashboardEventStore.ComposeBatchAsync"/> returns an
    ///     all-null bundle on any failure, while a genuine empty window yields non-null empty
    ///     lists. A partial bundle (some slices null) is acceptable: the render helpers
    ///     self-fetch the missing slices.
    /// </summary>
    internal static bool HasAnyRequestedData(DashboardDatasetBundle bundle, IReadOnlyList<DatasetKind> requested)
    {
        foreach (var kind in requested)
        {
            if (kind switch
            {
                DatasetKind.SummaryStats => bundle.Summary is not null,
                DatasetKind.TimeBuckets => bundle.TimeBuckets is not null,
                DatasetKind.BotAggregate => bundle.BotAggregate is not null,
                DatasetKind.GeoBreakdown => bundle.Geo is not null,
                DatasetKind.EndpointStats => bundle.Endpoints is not null,
                DatasetKind.DegradationHistory => bundle.Degradations is not null,
                _ => false
            })
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    ///     Stashes the warm page bundle (kept fresh by the tick materializer) for the
    ///     traffic page at the update's window, so delta widgets read a ready bundle
    ///     instead of composing in-request. The traffic manifest carries all five
    ///     dataset kinds, so any covered widget finds its slice. Returns false when no
    ///     content cache / manifest is available (caller falls back to a subset
    ///     compose); read failures degrade to the same fallback.
    /// </summary>
    internal static async Task<bool> TryStashWarmPageBundleAsync(
        IDashboardContentCache? contentCache,
        IDashboardPageManifestSource? manifests,
        HttpContext context,
        DashboardPageWindow window,
        ILogger logger,
        CancellationToken ct)
    {
        if (contentCache is null || manifests is null)
            return false;

        var manifest = manifests.For("dashboard.traffic");
        if (manifest is null)
            return false;

        try
        {
            var page = await contentCache.GetCurrentAsync(manifest, window, ct);
            // Never stash a Warming placeholder as a real bundle: the per-widget renderers
            // treat a present stash as "real data, no self-fetch needed", so stashing
            // DashboardPageResult.Warming would render empty shingles and cache them as
            // authoritative (the L2 pre-render's cold-L1 race — the empty shingles then
            // made the boot gate latch with nothing to serve). A Warming result falls
            // through to the compose-on-delta subset instead, which fetches real data.
            if (page.IsWarming) return false;
            context.Items["sb.dashboard.pageresult"] = page;
            return true;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "SbWidgetBatch: warm page-bundle read failed — falling back to subset compose");
            return false;
        }
    }

    /// <summary>
    ///     Builds a <see cref="DashboardPageWindow"/> from the update request's
    ///     query params (window, domain). Mirrors how <c>TrafficController.Index</c>
    ///     constructs its window so batch-updated widgets use the same data slice as
    ///     the page they live on -- including its hard-coded all-audience compose.
    ///     Internal for regression coverage (SbTopBotsBatchPathDomainScopeTests).
    /// </summary>
    internal static DashboardPageWindow BuildBatchWindow(HttpContext context)
    {
        var q = context.Request.Query;

        // The client forwards each widget's params PREFIXED (widget.key=val) -- see
        // sb-live-updates.js flush(). All widgets on one page share the page's window, so
        // fall back to the first prefixed *.window param when no bare window is present;
        // otherwise every beacon refresh would read the 6h default while the page shows
        // 24h, and the OOB swap would replace the page's widgets with a different window's
        // data. A bare window= still wins (drill/chartlet requests send one).
        var windowToken = q["window"].FirstOrDefault()
            ?? q.Keys.Where(k => k.EndsWith(".window", StringComparison.Ordinal))
                .Select(k => q[k].FirstOrDefault())
                .FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))
            ?? "6h";
        var windowMinutes = ParseWindowToken(windowToken);
        var now = DateTime.UtcNow;
        var startTime = now.AddMinutes(-windowMinutes);

        var domains = q["domain"]
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        // Dashboard-wide domain scope (DI seam) — keep the live HTMX widget refresh
        // in lockstep with the SSR page window (BuildVisitorsPageWindow). FOSS default
        // returns null (no-op), so the query["domain"] path is unchanged; a non-null
        // seam result wins over the query param.
        var scopedDomains = context.RequestServices
            .GetService<IDashboardDomainScope>()
            ?.GetSelectedDomains(context);
        IReadOnlyList<string>? domainsFilter =
            scopedDomains is { Count: > 0 } ? scopedDomains
            : domains.Length > 0 ? domains : null;

        // Use the same bucket width the TrafficController uses for a comparable window.
        var bucketSize = HitsPerPeriodChartletBuilder.BucketSizeForWindow(windowToken);

        // Compose the shared page bundle ALL-AUDIENCE, matching TrafficController /
        // VisitorsController (both hard-code "all") and the tick materializer's pinned
        // prewarm (DashboardMaterializerCoordinator uses "all"). This is load-bearing for
        // the Top Bots header: its All/Bots/Humans/Internal chips and client-side audience
        // switch need the FULL distribution, and GetTopBotsAsync maps a null audience to
        // BOTS-ONLY (legacy back-compat) -- so composing null here produced a bots-only
        // BotAggregate whose header read Humans=0/Internal=0, making a scoped all-audience
        // self-fetch out-count it (scoped > unscoped). It also keyed the compose to a null
        // audience while DashboardContentEnvelope normalizes null->"all", poisoning the shared
        // "all" cache entry with bots-only rows. A targeted audience chip ("bots"/"humans"/
        // "all_incl_internal") never reads this composed bundle anyway -- every render helper
        // routes those through the store directly (see Render*Async above) -- so hard-coding
        // "all" here only ever widens the composed set, never hides a requested slice.
        return new DashboardPageWindow(
            StartTime: startTime,
            EndTime: now,
            AudienceFilter: "all",
            ProbMin: null,
            Domains: domainsFilter,
            TopN: 500,
            BucketMinutes: (int)bucketSize.TotalMinutes);
    }

    private static int ParseWindowToken(string token) => token switch
    {
        "15m"         => 15,
        "60m" or "1h" => 60,
        "6h"          => 6 * 60,
        "12h"         => 12 * 60,
        "24h" or "1d" => 24 * 60,
        "7d"          => 7 * 24 * 60,
        "30d"         => 30 * 24 * 60,
        _             => DefaultBatchWindowMinutes,
    };

    // -------------------------------------------------------------------------
    // Cache + render
    // -------------------------------------------------------------------------

    private async Task<string> RenderWidgetWithShingleAsync(HttpContext context, string widgetId, string fingerprint)
    {
        // Warm shingle: serve the resident OOB element as-is (the read already boosted its
        // LFU score in the partition pass; a concurrent request may also have warmed it
        // between the partition and here, so re-check).
        if (_shingleCache.TryGet(fingerprint, out var cached) && !string.IsNullOrEmpty(cached))
            return cached;

        // Miss: render the widget, tag it OOB, and store the shingle under its fingerprint.
        // The version in the fingerprint means this shingle stays valid until this widget's
        // surface next changes -- no TTL churn, no whole-page recompute.
        var q = WidgetRenderHelpers.ExtractWidgetParams(context, widgetId);
        var html = await RenderWidgetAsync(context, widgetId, q);
        if (!string.IsNullOrEmpty(html))
        {
            html = WidgetRenderHelpers.InjectOobAttribute(html);
            _shingleCache.Set(fingerprint, html);
        }

        return html ?? "";
    }

    private async Task<string> RenderWidgetAsync(HttpContext context, string widgetId, IQueryCollection q)
    {
        try
        {
            return widgetId switch
            {
                "summary" => await RenderSummaryAsync(context),
                "visitors" => await RenderVisitorsAsync(context, q),
                "countries" => await RenderCountriesAsync(context, q),
                "endpoints" => await RenderEndpointsAsync(context, q),
                "useragents" => await RenderUserAgentsAsync(context, q),
                "sessions" => await RenderSessionsAsync(context, q),
                "topbots" or "top-visitors" or "live-visitors" or "live-activity" or "overview-topbots" => await RenderTopBotsAsync(context, q, widgetId),
                "threats" => await RenderThreatsAsync(context, q),
                "traffic-panels" => await RenderTrafficPanelsAsync(context, q),
                _ => ""
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SbWidgetBatch: failed to render widget '{Widget}'", widgetId);
            return "";
        }
    }

    // -------------------------------------------------------------------------
    // Per-widget render helpers
    // -------------------------------------------------------------------------

    private async Task<string> RenderSummaryAsync(HttpContext context)
    {
        // Honor the audience filter so a humans-only render produces a
        // humans-only KPI strip. Mirrors StyloBotDashboardMiddleware.BuildSummaryStatsModelAsync.
        var audienceFilter = (context.Request.Query["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();

        // If the compose-on-delta path already fetched the summary dataset, use it directly
        // (zero additional store calls). Fall back to self-fetch when not present (e.g. the
        // composer is not registered, compose failed, or audience filter needs a targeted query).
        DashboardSummary? summary = null;
        var pageResult = context.Items["sb.dashboard.pageresult"] as DashboardPageResult;
        if (pageResult?.Summary is { } composedSummary && audienceFilter is not ("humans" or "bots"))
        {
            summary = composedSummary;
        }
        else
        {
            var audienceArg = audienceFilter is "humans" or "bots" ? audienceFilter : null;
            summary = await _eventStore.GetSummaryAsync(audienceFilter: audienceArg);
        }
        var model = new SummaryStatsModel { Summary = summary, BasePath = _options.BasePath.TrimEnd('/') };

        var signatureCache = context.RequestServices.GetService<SignatureAggregateCache>();
        if (signatureCache != null)
            PopulateSessionAnalytics(model, signatureCache);

        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbSummaryStats/Default.cshtml", model, context);
    }

    private async Task<string> RenderVisitorsAsync(HttpContext context, IQueryCollection q)
    {
        var signatureCache = context.RequestServices.GetRequiredService<SignatureAggregateCache>();
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "lastSeen";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var (items, totalCount, _, _) = signatureCache.GetFiltered(filter, sortField, sortDir, page, 24);

        // Plan task 19: same gateway-projected drift badge enrichment every
        // other visitor-list render path runs. Keeps the batch widget endpoint
        // in agreement with /partials/visitors and the SSR shell.
        await FingerprintDriftProjector.EnrichVisitorsAsync(
            items, context.RequestServices, context.RequestAborted);

        var model = new VisitorListModel
        {
            Visitors = items,
            Counts = signatureCache.GetVisitorCounts(),
            Filter = filter,
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = 24,
            TotalCount = totalCount,
            BasePath = _options.BasePath.TrimEnd('/')
        };
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbVisitorList/Default.cshtml", model, context);
    }

    private async Task<string> RenderCountriesAsync(HttpContext context, IQueryCollection q)
    {
        var sortField = q["sort"].FirstOrDefault() ?? "total";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var audienceFilter = (q["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();

        // Use the composed Geo slice when available (zero additional store calls).
        // Fall back to the existing cache-first / store path for targeted audience queries
        // or when the composer is not registered / compose failed.
        List<DashboardCountryStats> data;
        var pageResult = context.Items["sb.dashboard.pageresult"] as DashboardPageResult;
        if (pageResult?.Geo is { } composedGeo && audienceFilter is not ("humans" or "bots"))
        {
            data = composedGeo.ToList();
        }
        else if (audienceFilter is "humans" or "bots")
        {
            // humans/bots route through the store so the is_bot SQL predicate applies.
            data = await _eventStore.GetCountryStatsAsync(100, audienceFilter: audienceFilter);
        }
        else
        {
            data = _aggregateCache.Current.Countries is { Count: > 0 } cached
                ? cached
                : await _eventStore.GetCountryStatsAsync(100);
        }
        var model = BuildCountriesModel(sortField, sortDir, page, 20, data);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbCountriesList/Default.cshtml", model, context);
    }

    private async Task<string> RenderEndpointsAsync(HttpContext context, IQueryCollection q)
    {
        var sortField = q["sort"].FirstOrDefault() ?? "total";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 25);
        var audienceFilter = (q["audience"].FirstOrDefault() ?? "all").Trim().ToLowerInvariant();

        // Use the composed Endpoints slice when available (zero additional store calls).
        // Fall back to the existing cache-first / store path for targeted audience queries.
        List<DashboardEndpointStats> data;
        var pageResult = context.Items["sb.dashboard.pageresult"] as DashboardPageResult;
        // "all_incl_internal" (the endpoint control's "Show self-probe" toggle) must also
        // route through the store: BuildBatchWindow only ever composes "bots"/"humans"/null
        // (see its audience parse above), so a composed bundle can never satisfy an
        // "include Internal" request -- it was composed with Internal already excluded.
        var storeFilters = audienceFilter is "humans" or "bots" or "honeypot" or "all_incl_internal";
        if (pageResult?.Endpoints is { } composedEndpoints && !storeFilters)
        {
            data = composedEndpoints.ToList();
        }
        else if (storeFilters)
        {
            // humans / bots / honeypot / all_incl_internal all require the store path.
            // honeypot needs IsHoneypot populated per row by the path classifier; humans /
            // bots need the SQL is_bot predicate; all_incl_internal needs the Internal rows
            // the composed/cached snapshots never carry.
            data = await _eventStore.GetEndpointStatsAsync(100, audienceFilter: audienceFilter);
        }
        else
        {
            data = _aggregateCache.Current.Endpoints is { Count: > 0 } cached
                ? cached
                : await _eventStore.GetEndpointStatsAsync(100);
        }
        var model = BuildEndpointsModel(sortField, sortDir, page, pageSize, data);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbEndpointsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderUserAgentsAsync(HttpContext context, IQueryCollection q)
    {
        var filter = q["filter"].FirstOrDefault() ?? "all";
        var sortField = q["sort"].FirstOrDefault() ?? "requests";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);

        // Use the aggregate cache (populated by DashboardSummaryBroadcaster).
        // IDashboardEventStore does not expose a GetUserAgentStatsAsync method, so no DB
        // fallback is available when the cache is empty (e.g. immediately after startup
        // before DashboardSummaryBroadcaster has run its first tick).
        var all = _aggregateCache.Current.UserAgents;
        var model = BuildUserAgentsModel(filter, sortField, sortDir, page, 25, all);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbUserAgentsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderSessionsAsync(HttpContext context, IQueryCollection q)
    {
        var filter = q["filter"].FirstOrDefault();
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 25);
        var model = await BuildSessionsModelAsync(context, page, pageSize, filter);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbSessionsList/Default.cshtml", model, context);
    }

    private async Task<string> RenderTopBotsAsync(HttpContext context, IQueryCollection q, string routeWidgetId = "topbots")
    {
        var sortBy = q["sort"].FirstOrDefault() ?? "default";
        var sortDir = q["dir"].FirstOrDefault() ?? "desc";
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 10, 50);
        var filter = q["filter"].FirstOrDefault() ?? "bots";
        var widgetId = q["widgetId"].FirstOrDefault() ?? routeWidgetId;
        var searchQuery = q["q"].FirstOrDefault();

        // Use the composed BotAggregate slice when available (zero additional store calls).
        // The composed bundle is now all-audience (BuildBatchWindow hard-codes "all"), so its
        // BotAggregate carries the full distribution the header + client-side chips need.
        var pageResult = context.Items["sb.dashboard.pageresult"] as DashboardPageResult;
        var composedBots = pageResult?.BotAggregate;
        // Dashboard-wide domain scope (DI seam): thread the selected domains into the
        // self-fetch fallback (used when no composed bundle is present) so a scoped render
        // subsets the store read instead of returning all-domain rows.
        var scopedDomains = context.RequestServices
            .GetService<IDashboardDomainScope>()
            ?.GetSelectedDomains(context);
        var model = await BuildTopBotsModel(page, pageSize, sortBy, sortDir, filter, widgetId, searchQuery, composedBots, scopedDomains);
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbTopBots/Default.cshtml", model, context);
    }

    private async Task<string> RenderThreatsAsync(HttpContext context, IQueryCollection q)
    {
        var page = WidgetRenderHelpers.QueryPage(q);
        var pageSize = WidgetRenderHelpers.QueryPageSize(q, 20, 100);

        List<ThreatEntry> allThreats;
        try { allThreats = await _eventStore.GetThreatsAsync(pageSize * 10); }
        catch { allThreats = []; }

        var totalCount = allThreats.Count;
        var pagedThreats = allThreats.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        var model = new ThreatsListModel
        {
            Threats = pagedThreats,
            TotalCount = totalCount,
            ActiveHoneypotSessions = pagedThreats.Count(t => t.InHoneypot),
            Page = page,
            PageSize = pageSize,
            BasePath = _options.BasePath.TrimEnd('/')
        };
        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/Shared/Components/SbThreatsList/Default.cshtml", model, context);
    }

    // -------------------------------------------------------------------------
    // Traffic side panels (content-ready OOB refresh)
    // -------------------------------------------------------------------------

    /// <summary>
    ///     Re-renders the four Traffic side panels (by country, by source, top
    ///     visitors, threats) for the content-ready OOB swap
    ///     (<c>data-sb-widget="traffic-panels"</c> on <c>#traffic-panels</c>).
    ///     Reads the warm page bundle the compose-on-delta path stashed
    ///     (<see cref="TryStashWarmPageBundleAsync"/> serves the tick-materialized
    ///     dashboard.traffic bundle for the update's window) and projects it through
    ///     <see cref="TrafficPanelsProjector"/> — the SAME projection the SSR first
    ///     paint uses (<c>_Traffic.cshtml</c>) — so the swap can never disagree with
    ///     the initial render. Filters come from the widget's own data-sb-params
    ///     (window/country/bot_type/threat), forwarded by the client verbatim.
    ///     Returns empty (no swap) when no bundle is stashed yet — the page keeps
    ///     its current (possibly warming) DOM and the next beacon retries.
    /// </summary>
    private async Task<string> RenderTrafficPanelsAsync(HttpContext context, IQueryCollection q)
    {
        var pageResult = context.Items["sb.dashboard.pageresult"] as DashboardPageResult;
        if (pageResult is null || pageResult.IsWarming) return "";

        // The panels need the BotAggregate + Geo slices. A bundle that lacks them (e.g. a
        // delta compose that didn't request Geo, or a partial compose) must NOT render
        // empty panels as authoritative — that caches zero-shingles the same way the
        // all-null compose-failure bundle did (prod P0). Return no swap instead: the page
        // keeps its current (SSR, store-read) DOM and the next beacon retries with the
        // warm traffic bundle, which always carries both slices.
        if (pageResult.BotAggregate is null || pageResult.Geo is null) return "";

        var layout = context.RequestServices
            .GetService<Microsoft.Extensions.Options.IOptions<Models.Dashboard.Layout.DashboardLayoutOptions>>()
            ?.Value;
        var topN = layout?.TrafficCardTopN ?? 10;
        var threatsOptions = context.RequestServices
            .GetService<Microsoft.Extensions.Options.IOptions<ThreatsOptions>>()?.Value ?? new ThreatsOptions();
        string? Nullable(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
        var windowToken = q["window"].FirstOrDefault() ?? DefaultWindowTokenFor(layout);

        var filters = new Models.Dashboard.Traffic.TrafficFilters(
            Country: Nullable(q["country"].FirstOrDefault()),
            BotType: Nullable(q["bot_type"].FirstOrDefault()),
            Window: windowToken,
            Threat: Nullable(q["threat"].FirstOrDefault()));

        // Same post-filter projection semantics as _Traffic.cshtml (shared helper).
        var (visitors, _, _) = WidgetRenderHelpers.ProjectAsVisitors(
            pageResult.BotAggregate ?? [],
            filter: "all", sortField: "hits", sortDir: "desc",
            page: 1, pageSize: 500,
            country: filters.Country, botType: filters.BotType, threat: filters.Threat);

        var panels = Models.Dashboard.Traffic.TrafficPanelsProjector.Project(
            visitors, pageResult.Geo ?? [], threatsOptions, topN);

        // The panels partial only reads the four projected lists + Filters/BasePath/
        // IsWarming; counters/timeseries/families are chart data this widget never
        // renders, so they stay as empty placeholders rather than recomputed values.
        var model = new Models.Dashboard.Traffic.TrafficPageModel(
            Filters: filters,
            Counters: new Models.Dashboard.Traffic.TrafficCounters(0, 0, 0, 0, 0, 0, 0, 0, 0, 0),
            Timeseries: new Models.Dashboard.Traffic.TrafficTimeseries([], [], [], []),
            BotFamilies: new Models.Dashboard.Traffic.BotFamilySeries([], []),
            Countries: panels.Countries,
            BotTypes: panels.BotTypes,
            TopEndpoints: [],
            TopVisitors: panels.TopVisitors,
            Threats: panels.Threats,
            BasePath: _options.BasePath.TrimEnd('/'));

        return await _razorViewRenderer.RenderViewToStringAsync(
            "/Views/StyloBot/Dashboard/Traffic/_TrafficPanels.cshtml", model, context);
    }

    /// <summary>
    ///     The window token the Traffic page resolves for a plain (no query) visit —
    ///     same mapping as <c>_Traffic.cshtml</c>'s FormatWindow — so a fallback
    ///     render never drifts from the SSR's default period.
    /// </summary>
    private static string DefaultWindowTokenFor(Models.Dashboard.Layout.DashboardLayoutOptions? layout)
    {
        var minutes = layout?.DefaultTimeWindowMinutes ?? 1440;
        return minutes switch
        {
            15 => "15m",
            60 => "1h",
            360 => "6h",
            720 => "12h",
            1440 => "24h",
            7 * 24 * 60 => "7d",
            30 * 24 * 60 => "30d",
            _ => "24h",
        };
    }

    // -------------------------------------------------------------------------
    // Model builders (mirrors private methods in StyloBotDashboardMiddleware)
    // -------------------------------------------------------------------------

    private async Task<TopBotsListModel> BuildTopBotsModel(
        int page, int pageSize, string sortBy, string sortDir,
        string filter = "bots", string widgetId = "topbots", string? searchQuery = null,
        IReadOnlyList<DashboardTopBotEntry>? composedBots = null,
        IReadOnlyList<string>? domains = null)
    {
        // Use the composed BotAggregate when present (supplied by the compose-on-delta path).
        // Otherwise fall back to the read-through-event-store pattern so the widget renders
        // correctly even when the composer is not in use (e.g. non-batched direct requests).
        // Unfiltered (audience="all") fetch so the All/Bots/Humans header counts reflect the
        // full distribution -- audience switch is applied client-side. Domain scope is threaded
        // so a scoped render subsets the store read (GetTopBotsWindowedAsync WHERE domain IN ...)
        // and its header counts stay a true subset of the all-domain view.
        IReadOnlyList<DashboardTopBotEntry> raw;
        if (composedBots is not null)
        {
            raw = composedBots;
        }
        else
        {
            raw = await _eventStore.GetTopBotsAsync(
                count: _signatureCache.MaxEntries,
                startTime: DateTime.UtcNow.AddHours(-24),
                endTime: DateTime.UtcNow,
                audienceFilter: "all",
                domains: domains);
        }
        // The event store never carries the per-minute HitTrend ring buffer (DB stores raw
        // detections, not per-minute counts) -- splice it in from the gateway's own live
        // SignatureAggregateCache before collapsing, so the SignalR/batch Top Bots sparklines
        // draw real activity instead of a permanently flat baseline. Covers both the composed
        // and self-fetch branches above since it runs after both converge on `raw`. See
        // WidgetRenderHelpers.OverlayLiveHitTrend's doc comment for the full rationale.
        raw = WidgetRenderHelpers.OverlayLiveHitTrend(raw, _signatureCache);
        // Internal = network-trusted operator/self traffic (loopback / RFC1918 /
        // docker bridge -> BotType.Internal). Hidden from the All / Bots / Humans
        // chips by default because it's almost always StyloBot Internal hitting
        // its own dashboard endpoints and OTel collector noise that drowns out
        // the rows the operator actually wants to triage. The dedicated "Internal"
        // chip surfaces it on demand.
        static bool IsInternal(DashboardTopBotEntry e) =>
            string.Equals(e.BotType, "Internal", StringComparison.OrdinalIgnoreCase);
        // One source of truth: collapse the raw distribution to visible identities
        // FIRST, then derive BOTH the header chips (All/Bots/Humans/Internal) AND the
        // list rows from that same collapsed set. Counting `raw` (pre-collapse) rows
        // while rendering a CollapseGroupableIdentities (post-collapse) list is what
        // detached the chips from the visible rows (operator P0: counts must reconcile
        // with the list, computed-at-read from ONE source -- never a second,
        // differently-shaped count). Must match BuildTopBotsModelFromRaw exactly so the
        // SignalR/batch live-update and the SSR first paint agree by construction.
        var collapsed = WidgetRenderHelpers.CollapseGroupableIdentities(raw);
        var publicTraffic = collapsed.Where(b => !IsInternal(b)).ToList();
        var internalCount = collapsed.Count - publicTraffic.Count;
        var bots = publicTraffic.Count(b => b.IsKnownBot);
        var humans = publicTraffic.Count - bots;
        IEnumerable<DashboardTopBotEntry> filtered = filter switch
        {
            "bots"     => publicTraffic.Where(b => b.IsKnownBot),
            "humans"   => publicTraffic.Where(b => !b.IsKnownBot),
            "internal" => collapsed.Where(IsInternal),
            _          => publicTraffic
        };
        var sorted = WidgetRenderHelpers.SortTopBots(filtered, sortBy, sortDir).ToList();
        var searched = WidgetRenderHelpers.ApplySearchFilter(sorted, searchQuery);
        var pagedBots = searched.Skip((page - 1) * pageSize).Take(pageSize).ToList();

        return new TopBotsListModel
        {
            Bots = pagedBots,
            Page = page,
            PageSize = pageSize,
            TotalCount = searched.Count,
            SortField = sortBy,
            SortDir = sortDir,
            BasePath = _options.BasePath.TrimEnd('/'),
            Filter = filter,
            WidgetId = widgetId,
            Counts = new TopBotsCounts(All: publicTraffic.Count, Bots: bots, Humans: humans, Internal: internalCount),
            Query = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim(),
        };
    }

    private EndpointsListModel BuildEndpointsModel(string sortField, string sortDir, int page, int pageSize, List<DashboardEndpointStats> all)
    {
        IEnumerable<DashboardEndpointStats> sorted = sortField.ToLowerInvariant() switch
        {
            "method" => sortDir == "asc" ? all.OrderBy(e => e.Method) : all.OrderByDescending(e => e.Method),
            "path" => sortDir == "asc" ? all.OrderBy(e => e.Path) : all.OrderByDescending(e => e.Path),
            "bots" => sortDir == "asc" ? all.OrderBy(e => e.BotCount) : all.OrderByDescending(e => e.BotCount),
            "botrate" => sortDir == "asc" ? all.OrderBy(e => e.BotRate) : all.OrderByDescending(e => e.BotRate),
            "latency" => sortDir == "asc" ? all.OrderBy(e => e.AvgProcessingTimeMs) : all.OrderByDescending(e => e.AvgProcessingTimeMs),
            "threat" => sortDir == "asc" ? all.OrderBy(e => e.AvgThreatScore) : all.OrderByDescending(e => e.AvgThreatScore),
            "unique" => sortDir == "asc" ? all.OrderBy(e => e.UniqueSignatures) : all.OrderByDescending(e => e.UniqueSignatures),
            "lastseen" => sortDir == "asc" ? all.OrderBy(e => e.LastSeen) : all.OrderByDescending(e => e.LastSeen),
            _ => sortDir == "asc" ? all.OrderBy(e => e.TotalCount) : all.OrderByDescending(e => e.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new EndpointsListModel
        {
            Endpoints = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count,
            AllowEndpointPinning = _options.EnableEndpointPinning,
        };
    }

    private UserAgentsListModel BuildUserAgentsModel(string filter, string sortField, string sortDir, int page, int pageSize, List<DashboardUserAgentSummary> all)
    {
        IEnumerable<DashboardUserAgentSummary> filtered = filter switch
        {
            "browser" => all.Where(u => u.Category == "Browser"),
            "bot" => all.Where(u => u.BotRate > 0.5),
            "ai" => all.Where(u => u.Category is "AI" or "AiBot"),
            "tool" => all.Where(u => u.Category is "Tool" or "Scraper" or "MonitoringBot"),
            _ => all
        };
        var filteredList = filtered.ToList();
        IEnumerable<DashboardUserAgentSummary> sorted = sortField.ToLowerInvariant() switch
        {
            "family" => sortDir == "asc" ? filteredList.OrderBy(u => u.Family) : filteredList.OrderByDescending(u => u.Family),
            "category" => sortDir == "asc" ? filteredList.OrderBy(u => u.Category) : filteredList.OrderByDescending(u => u.Category),
            "botrate" => sortDir == "asc" ? filteredList.OrderBy(u => u.BotRate) : filteredList.OrderByDescending(u => u.BotRate),
            "confidence" => sortDir == "asc" ? filteredList.OrderBy(u => u.AvgConfidence) : filteredList.OrderByDescending(u => u.AvgConfidence),
            "lastseen" => sortDir == "asc" ? filteredList.OrderBy(u => u.LastSeen) : filteredList.OrderByDescending(u => u.LastSeen),
            _ => sortDir == "asc" ? filteredList.OrderBy(u => u.TotalCount) : filteredList.OrderByDescending(u => u.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new UserAgentsListModel
        {
            UserAgents = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            Filter = filter,
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = filteredList.Count
        };
    }

    private CountriesListModel BuildCountriesModel(string sortField, string sortDir, int page, int pageSize, List<DashboardCountryStats> all)
    {
        IEnumerable<DashboardCountryStats> sorted = sortField.ToLowerInvariant() switch
        {
            "country" => sortDir == "asc" ? all.OrderBy(c => c.CountryCode) : all.OrderByDescending(c => c.CountryCode),
            "botrate" => sortDir == "asc" ? all.OrderBy(c => c.BotRate) : all.OrderByDescending(c => c.BotRate),
            "bots" => sortDir == "asc" ? all.OrderBy(c => c.BotCount) : all.OrderByDescending(c => c.BotCount),
            "humans" => sortDir == "asc" ? all.OrderBy(c => c.HumanCount) : all.OrderByDescending(c => c.HumanCount),
            _ => sortDir == "asc" ? all.OrderBy(c => c.TotalCount) : all.OrderByDescending(c => c.TotalCount)
        };
        var paged = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new CountriesListModel
        {
            Countries = paged,
            BasePath = _options.BasePath.TrimEnd('/'),
            SortField = sortField,
            SortDir = sortDir,
            Page = page,
            PageSize = pageSize,
            TotalCount = all.Count
        };
    }

    private async Task<SessionsListModel> BuildSessionsModelAsync(HttpContext context, int page, int pageSize, string? filter)
    {
        var sessionStore = context.RequestServices.GetService<IDetectionArchive>();
        if (sessionStore == null)
        {
            return new SessionsListModel
            {
                Sessions = [],
                BasePath = _options.BasePath.TrimEnd('/'),
                Filter = filter
            };
        }

        bool? isBot = filter switch { "bot" => true, "human" => false, _ => null };
        const int maxFetch = 500;
        var fetchCount = Math.Min(page * pageSize + pageSize, maxFetch);
        var since = DateTime.UtcNow - _options.DetectionRetention;
        var sessions = await sessionStore.GetRecentSessionsAsync(fetchCount, isBot, since);
        var totalCount = sessions.Count < maxFetch ? sessions.Count : maxFetch;

        var sigLookup = await _eventStore.LoadSignatureLookupAsync();
        var uaLookup  = await _eventStore.LoadUserAgentLookupAsync();

        var entries = sessions
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(s => new SessionListEntry
        {
            Id = s.Id,
            Signature = s.Signature,
            StartedAt = s.StartedAt,
            EndedAt = s.EndedAt,
            RequestCount = s.RequestCount,
            DominantState = s.DominantState,
            IsBot = s.IsBot,
            AvgBotProbability = s.AvgBotProbability,
            RiskBand = s.RiskBand,
            Action = s.Action,
            BotName = sigLookup.ResolveBotName(_signatureCache, s.Signature, s.BotName),
            CountryCode = s.CountryCode,
            UserAgent = uaLookup.ResolveUserAgent(_signatureCache, s.Signature),
            ErrorCount = s.ErrorCount,
            TimingEntropy = s.TimingEntropy,
            Maturity = s.Maturity,
            TransitionCounts = s.TransitionCountsJson != null
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(s.TransitionCountsJson)
                : null
        }).ToList();

        return new SessionsListModel
        {
            Sessions = entries,
            BasePath = _options.BasePath.TrimEnd('/'),
            Page = page,
            PageSize = pageSize,
            TotalCount = totalCount,
            Filter = filter
        };
    }

    // -------------------------------------------------------------------------
    // POST /partials/render — Liquid widget rendering for Node SDK
    // -------------------------------------------------------------------------

    private async Task HandleLiquidRenderAsync(HttpContext context)
    {
        context.Response.ContentType = "text/html; charset=utf-8";

        if (context.Request.ContentLength > 64_000)
        {
            context.Response.StatusCode = 413;
            return;
        }

        Dictionary<string, string>? body;
        try
        {
            using var doc = await JsonDocument.ParseAsync(context.Request.Body);
            if (!doc.RootElement.TryGetProperty("widgets", out var widgetsEl))
            {
                context.Response.StatusCode = 400;
                return;
            }
            body = widgetsEl.EnumerateObject()
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
        }
        catch
        {
            context.Response.StatusCode = 400;
            return;
        }

        var sb = new StringBuilder();
        foreach (var (widgetId, template) in body)
        {
            var q = WidgetRenderHelpers.ExtractWidgetParams(context, widgetId);
            string html = string.IsNullOrWhiteSpace(template)
                ? await RenderWidgetAsync(context, widgetId, q)
                : await RenderLiquidWidgetAsync(context, widgetId, template) ?? "";

            if (!string.IsNullOrEmpty(html))
            {
                html = WidgetRenderHelpers.InjectOobAttribute(html);
                sb.Append(html);
            }
        }

        await context.Response.WriteAsync(sb.ToString());
    }

    private async Task<string?> RenderLiquidWidgetAsync(
        HttpContext context, string widgetId, string template)
    {
        try
        {
            var data = await BuildLiquidContextAsync(context, widgetId);
            return data == null ? null
                : await _liquidRenderer.RenderAsync(widgetId, template, data);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SbWidgetBatch: Liquid render failed for '{Widget}'", widgetId);
            return null;
        }
    }

    private async Task<Dictionary<string, object>?> BuildLiquidContextAsync(
        HttpContext context, string widgetId)
    {
        try
        {
            return widgetId switch
            {
                "summary" => await BuildSummaryContextAsync(),
                "topbots" or "top-visitors" or "live-visitors" or "live-activity" => await BuildTopBotsContextAsync(),
                "visitors" => BuildVisitorsContext(context),
                "countries" => await BuildCountriesContextAsync(),
                "endpoints" => await BuildEndpointsContextAsync(),
                "useragents" => BuildUserAgentsContext(),
                "threats" => await BuildThreatsContextAsync(),
                _ => new Dictionary<string, object>()
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex,
                "SbWidgetBatch: BuildLiquidContext failed for '{Widget}'", widgetId);
            return null;
        }
    }

    private async Task<Dictionary<string, object>> BuildSummaryContextAsync()
    {
        var summary = await _eventStore.GetSummaryAsync();
        return new Dictionary<string, object>
        {
            ["bot_requests"] = summary.BotRequests,
            ["human_requests"] = summary.HumanRequests,
            ["total_requests"] = summary.TotalRequests,
            ["uncertain_requests"] = summary.UncertainRequests,
            ["bot_rate"] = summary.TotalRequests > 0
                ? (double)summary.BotRequests / summary.TotalRequests : 0d,
            ["bot_percentage"] = summary.BotPercentage,
            ["unique_signatures"] = summary.UniqueSignatures,
            ["bot_fingerprints"] = summary.BotFingerprints,
            ["human_fingerprints"] = summary.HumanFingerprints,
            ["high_risk_fingerprints"] = summary.HighRiskFingerprints,
            ["avg_processing_ms"] = summary.AverageProcessingTimeMs,
        };
    }

    private async Task<Dictionary<string, object>> BuildTopBotsContextAsync()
    {
        var allBots = _signatureCache.GetTopBots(
            page: 1, pageSize: 50, sortBy: "default", sortDir: "desc", filter: "bots");

        var bots = allBots.Select(b => new Dictionary<string, object?>
        {
            ["signature_id"] = b.PrimarySignature,
            ["bot_name"] = b.BotName,
            ["bot_type"] = b.BotType,
            ["risk_band"] = b.RiskBand,
            ["hit_count"] = b.HitCount,
            ["bot_probability"] = b.BotProbability,
            ["action"] = b.Action,
            ["country_code"] = b.CountryCode,
            ["last_seen"] = b.LastSeen.ToString("O"),
        }).ToList();

        return new Dictionary<string, object> { ["bots"] = bots };
    }

    private Dictionary<string, object> BuildVisitorsContext(HttpContext context)
    {
        var signatureCache = context.RequestServices.GetService<SignatureAggregateCache>();
        if (signatureCache == null)
            return new Dictionary<string, object> { ["visitors"] = new List<object>() };

        var (items, totalCount, _, _) = signatureCache.GetFiltered("all", "lastSeen", "desc", 1, 50);
        var visitors = items.Select(v => new Dictionary<string, object?>
        {
            ["signature_id"] = v.PrimarySignature,
            ["is_bot"] = v.IsBot,
            ["risk_band"] = v.RiskBand,
            ["bot_name"] = v.BotName,
            ["bot_type"] = v.BotType,
            ["hits"] = v.Hits,
            ["country_code"] = v.CountryCode,
            ["last_seen"] = v.LastSeen.ToString("O"),
        }).ToList();

        return new Dictionary<string, object>
        {
            ["visitors"] = visitors,
            ["total_count"] = totalCount,
        };
    }

    private async Task<Dictionary<string, object>> BuildCountriesContextAsync()
    {
        var cached = _aggregateCache.Current.Countries;
        var data = cached.Count > 0 ? cached : await _eventStore.GetCountryStatsAsync(50);

        var countries = data.Select(c => new Dictionary<string, object?>
        {
            ["country_code"] = c.CountryCode,
            ["country_name"] = c.CountryName,
            ["total_count"] = c.TotalCount,
            ["bot_count"] = c.BotCount,
            ["human_count"] = c.HumanCount,
            ["bot_rate"] = c.BotRate,
        }).ToList();

        return new Dictionary<string, object> { ["countries"] = countries };
    }

    private async Task<Dictionary<string, object>> BuildEndpointsContextAsync()
    {
        var cached = _aggregateCache.Current.Endpoints;
        var data = cached.Count > 0 ? cached : await _eventStore.GetEndpointStatsAsync(50);

        var endpoints = data.Select(e => new Dictionary<string, object?>
        {
            ["method"] = e.Method,
            ["path"] = e.Path,
            ["total_count"] = e.TotalCount,
            ["bot_count"] = e.BotCount,
            ["human_count"] = e.HumanCount,
            ["bot_rate"] = e.BotRate,
            ["unique_signatures"] = e.UniqueSignatures,
            ["avg_processing_ms"] = e.AvgProcessingTimeMs,
            ["avg_threat_score"] = e.AvgThreatScore,
            ["last_seen"] = e.LastSeen.ToString("O"),
        }).ToList();

        return new Dictionary<string, object> { ["endpoints"] = endpoints };
    }

    private Dictionary<string, object> BuildUserAgentsContext()
    {
        var all = _aggregateCache.Current.UserAgents;
        var useragents = all.Select(u => new Dictionary<string, object?>
        {
            ["family"] = u.Family,
            ["category"] = u.Category,
            ["total_count"] = u.TotalCount,
            ["bot_count"] = u.BotCount,
            ["human_count"] = u.HumanCount,
            ["bot_rate"] = u.BotRate,
            ["avg_confidence"] = u.AvgConfidence,
            ["last_seen"] = u.LastSeen.ToString("O"),
        }).ToList();

        return new Dictionary<string, object> { ["useragents"] = useragents };
    }

    private async Task<Dictionary<string, object>> BuildThreatsContextAsync()
    {
        List<ThreatEntry> allThreats;
        try { allThreats = await _eventStore.GetThreatsAsync(50); }
        catch { allThreats = []; }

        var threats = allThreats.Select(t => new Dictionary<string, object?>
        {
            ["signature"] = t.Signature,
            ["path"] = t.Path,
            ["cve_id"] = t.CveId,
            ["cve_severity"] = t.CveSeverity,
            ["threat_score"] = t.ThreatScore,
            ["threat_band"] = t.ThreatBand,
            ["bot_name"] = t.BotName,
            ["bot_type"] = t.BotType,
            ["bot_probability"] = t.BotProbability,
            ["country_code"] = t.CountryCode,
            ["in_honeypot"] = t.InHoneypot,
            ["timestamp"] = t.Timestamp.ToString("O"),
        }).ToList();

        return new Dictionary<string, object> { ["threats"] = threats };
    }

    private static void PopulateSessionAnalytics(SummaryStatsModel model, SignatureAggregateCache signatureCache)
    {
        // pageSize > cache's MaxEntries returns the whole snapshot in one page.
        const int maxProjectedVisitors = 1_000;
        var (allVisitors, totalCount, _, _) = signatureCache.GetFiltered("all", "lastSeen", "desc", 1, maxProjectedVisitors);

        // Single pass to collect all counters — avoids 6 separate LINQ iterations over the same list.
        var activeThreshold = DateTime.UtcNow.AddMinutes(-5);
        int bots = 0, humans = 0, active = 0;
        int botSingles = 0, humanSingles = 0, allSingles = 0, allWithHits = 0;
        double botDurSum = 0, humanDurSum = 0, allDurSum = 0;
        int botDurCount = 0, humanDurCount = 0, allDurCount = 0;

        foreach (var v in allVisitors)
        {
            if (v.IsBot) bots++; else humans++;
            if (v.LastSeen > activeThreshold) active++;
            if (v.Hits > 0) allWithHits++;
            if (v.Hits == 1) { allSingles++; if (v.IsBot) botSingles++; else humanSingles++; }
            if (v.Hits > 1)
            {
                var dur = (v.LastSeen - v.FirstSeen).TotalSeconds;
                allDurSum += dur; allDurCount++;
                if (v.IsBot) { botDurSum += dur; botDurCount++; }
                else          { humanDurSum += dur; humanDurCount++; }
            }
        }

        model.UniqueVisitors  = totalCount;
        model.ActiveSessions  = active;
        model.BotSessions     = bots;
        model.HumanSessions   = humans;
        model.BounceRate      = allWithHits > 0 ? Math.Round((double)allSingles / allWithHits * 100, 1) : 0;
        model.HumanBounceRate = humans > 0 ? Math.Round((double)humanSingles / humans * 100, 1) : 0;
        model.BotBounceRate   = bots > 0 ? Math.Round((double)botSingles / bots * 100, 1) : 0;
        model.AvgSessionDurationSecs      = allDurCount > 0 ? Math.Round(allDurSum / allDurCount, 1) : 0;
        model.HumanAvgSessionDurationSecs = humanDurCount > 0 ? Math.Round(humanDurSum / humanDurCount, 1) : 0;
        model.BotAvgSessionDurationSecs   = botDurCount > 0 ? Math.Round(botDurSum / botDurCount, 1) : 0;
    }

}
