using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
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
        var (startTime, endTime) = AnalyticsRangeParser.Parse(range);

        IReadOnlyList<DashboardEndpointStats> data;
        // If the page composer stashed a DashboardPageResult, read the Endpoints slice from
        // it directly (zero store calls). Otherwise fall through to the existing cache-first /
        // parameter-driven self-fetch so VCs rendered on non-composer pages still work.
        var pageResult = HttpContext?.Items["sb.dashboard.pageresult"] as DashboardPageResult;
        if (pageResult?.Endpoints is { } composedEndpoints)
        {
            data = composedEndpoints;
        }
        else if (!string.IsNullOrEmpty(audience) || startTime.HasValue)
        {
            // Parameter-driven: always bypass cache, query store with the provided args.
            data = await eventStore.GetEndpointStatsAsync(500, startTime, endTime, audience);
        }
        else
        {
            // Legacy: cache-first / store-fallback so the live dashboard hot path is unchanged.
            var cached = aggregateCache.Current.Endpoints;
            data = cached.Count > 0 ? cached : await eventStore.GetEndpointStatsAsync(500);
        }
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
            AudienceFilter = string.IsNullOrEmpty(audience) ? "all" : audience,
            StatusFilter = string.IsNullOrEmpty(status) ? "all" : status,
            PathFilter = path ?? string.Empty,
            MethodFilter = string.IsNullOrEmpty(method) ? null : method,
            ModeFilter = string.IsNullOrEmpty(mode) ? null : mode,
            ThreatFilter = string.IsNullOrEmpty(threat) ? null : threat,
            BotPressureFilter = string.IsNullOrEmpty(botPressure) ? null : botPressure,
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
