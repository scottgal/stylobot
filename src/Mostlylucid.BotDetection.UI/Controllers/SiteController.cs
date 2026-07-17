using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Site;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>
///     Si1 + Si2 (dashboard IA collapse plan): owns the Site IA group.
///     <c>Index</c> renders the Site landing page (endpoints list + filter pills)
///     at <c>/dashboard/site</c>; <c>EndpointDetail</c> renders the per-endpoint
///     deep view at <c>/dashboard/site/endpoint?path=...&amp;method=...</c>.
///     The middleware passes both URLs through to MVC via the
///     <c>site</c> / <c>site/...</c> case in <c>StyloBotDashboardMiddleware</c>.
///
///     <para>
///     Legacy <c>/dashboard/endpoints</c> and <c>/dashboard/endpoint</c>
///     continue to render their own pages via the FOSS row registry; the
///     M-phase migration step is what 301-redirects the old URLs to the
///     new ones, not Si1 / Si2.
///     </para>
/// </summary>
[Route("dashboard/site")]
public sealed class SiteController : Controller
{
    private readonly IOptions<DashboardLayoutOptions> _layout;
    private readonly SignatureAggregateCache? _cache;
    private readonly IDashboardEventStore? _events;

    public SiteController(
        IOptions<DashboardLayoutOptions> layout,
        SignatureAggregateCache? cache = null,
        IDashboardEventStore? events = null)
    {
        _layout = layout;
        // Both deps are optional per feedback_remote_mode_optional_di: remote-mode
        // hosts (e.g. the marketing site) don't register the gateway-local
        // cache + event store; the endpoint detail view degrades gracefully to
        // an empty visitor list and a null perf card on those hosts.
        _cache = cache;
        _events = events;
    }

    [HttpGet("")]
    public IActionResult Index(
        [FromQuery] string? path,
        [FromQuery] string? method,
        [FromQuery] string? threat,
        [FromQuery(Name = "bot_pressure")] string? botPressure)
    {
        // The view populates Model.BasePath so the partial's HTMX urls + chip
        // links resolve under the dashboard mount, matching the same shape the
        // middleware uses. V2 / legacy share the same base path today; the
        // accessor is kept symmetrical with TrafficController + VisitorsController
        // so a future split (e.g. an embedded site) lands here without rewiring.
        var basePath = _layout.Value.V2Enabled
            ? "/dashboard"
            : "/dashboard";

        var model = new SitePageModel(
            Path: NullIfEmpty(path),
            Method: NullIfEmpty(method),
            Threat: NullIfEmpty(threat),
            BotPressure: NullIfEmpty(botPressure),
            BasePath: basePath);

        return View("/Views/StyloBot/Dashboard/Site/Index.cshtml", model);
    }

    /// <summary>
    ///     Si2: render the per-endpoint detail page. <paramref name="path"/> is
    ///     required (no path = nothing to drill into, redirect back to the
    ///     landing); <paramref name="method"/> defaults to GET because the
    ///     <see cref="SignatureAggregateCache"/> projections used by the
    ///     endpoints list don't carry method, mirroring
    ///     <c>TrafficController.TopByEndpoint</c> which uses the same default.
    /// </summary>
    [HttpGet("endpoint")]
    public async Task<IActionResult> EndpointDetail(
        [FromQuery] string? path,
        [FromQuery] string? method,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(path)) return RedirectToAction(nameof(Index));

        var opts = _layout.Value;
        var topN = opts.TrafficCardTopN;
        var windowMinutes = opts.DefaultTimeWindowMinutes;
        var resolvedMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();

        // Read from IDashboardEventStore as the canonical source so the
        // marketing-site host (no in-process SignatureAggregateCache) still
        // renders real visitor rows (per feedback_remote_mode_optional_di).
        // Cache fallback stays available for the rare host that has the LFU
        // but no event store -- never both as the source of truth.
        var visitors = await ResolveVisitorsForEndpointAsync(path, windowMinutes, ct);
        var perf = await TryResolvePerfAsync(resolvedMethod, path, ct);
        var scope = TryBuildEndpointScope(resolvedMethod, path);

        var model = new SiteEndpointDetailModel(
            Method: resolvedMethod,
            Path: path,
            Timeseries: BuildEndpointTimeseries(visitors, windowMinutes),
            TopVisitors: visitors.Take(topN).ToList(),
            PolicyScope: scope,
            Perf: perf);

        return View("/Views/StyloBot/Dashboard/Site/EndpointDetail.cshtml", model);
    }

    /// <summary>
    ///     Pull the visitor universe from <see cref="IDashboardEventStore.GetTopBotsAsync"/>
    ///     over the active window (canonical, shared change-detection source --
    ///     same path SbVisitorListViewComponent uses) and keep only rows whose
    ///     last observed path matches <paramref name="path"/>. The cache fallback
    ///     covers the rare host that has the LFU registered but no event store;
    ///     remote-mode hosts (marketing site) end up on the event-store path
    ///     because that's the only one registered. Returns an empty list when
    ///     neither is available -- the view treats empty as the no-visitor
    ///     empty-state.
    /// </summary>
    private async Task<IReadOnlyList<ProjectedVisitor>> ResolveVisitorsForEndpointAsync(
        string path, int windowMinutes, CancellationToken ct)
    {
        if (_events is not null)
        {
            try
            {
                var now = DateTime.UtcNow;
                var raw = await _events.GetTopBotsAsync(
                    count: 500,
                    startTime: now.AddMinutes(-windowMinutes),
                    endTime: now,
                    audienceFilter: "all");
                var (items, _, _) = WidgetRenderHelpers.ProjectAsVisitors(
                    raw, filter: "all", sortField: "lastSeen", sortDir: "desc",
                    page: 1, pageSize: 500);
                return items
                    .Where(v => string.Equals(v.LastPath, path, StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(v => v.Hits)
                    .ToList();
            }
            catch
            {
                // Fall through to the LFU fallback below.
            }
        }

        if (_cache is null) return Array.Empty<ProjectedVisitor>();

        var (all, _, _, _) = _cache.GetFiltered(
            filter: "all", sortField: "lastSeen", sortDir: "desc",
            page: 1, pageSize: 10_000);

        return all
            .Where(v => string.Equals(v.LastPath, path, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(v => v.Hits)
            .ToList();
    }

    /// <summary>
    ///     Bucket the matched visitor rows into a 60-bucket-max timeseries with
    ///     a parallel bot-only series. Mirrors <c>TrafficController.BuildTimeseries</c>
    ///     so the visual rhythm (bucket width / cap) stays consistent when the
    ///     operator pivots from the Traffic chart into a single endpoint.
    /// </summary>
    private static SiteEndpointTimeseries BuildEndpointTimeseries(
        IReadOnlyList<ProjectedVisitor> rows, int minutes)
    {
        var now = DateTime.UtcNow;
        var bucketCount = Math.Min(60, Math.Max(1, minutes));
        var bucketSizeMin = Math.Max(1, minutes / bucketCount);
        var buckets = new DateTime[bucketCount];
        var hits = new int[bucketCount];
        var bot = new int[bucketCount];
        for (var i = 0; i < bucketCount; i++)
            buckets[i] = now.AddMinutes(-minutes + i * bucketSizeMin);

        foreach (var v in rows)
        {
            var ageMin = (now - v.LastSeen).TotalMinutes;
            if (ageMin < 0 || ageMin > minutes) continue;
            var idx = bucketCount - 1 - (int)(ageMin / bucketSizeMin);
            if (idx < 0 || idx >= bucketCount) continue;
            hits[idx] += v.Hits;
            if (v.IsBot) bot[idx] += v.Hits;
        }
        return new SiteEndpointTimeseries(buckets, hits, bot);
    }

    /// <summary>
    ///     Pull the per-(method, path) row out of
    ///     <see cref="IDashboardEventStore.GetEndpointStatsAsync"/> and project
    ///     it to <see cref="EndpointPerf"/>. The store doesn't expose a p50
    ///     column today, so we use the avg as a proxy -- same shortcut the
    ///     legacy <c>_EndpointDetail</c> view takes. Returns null when the
    ///     store isn't registered or when no matching row exists.
    /// </summary>
    private async Task<SiteEndpointPerf?> TryResolvePerfAsync(string method, string path, CancellationToken ct)
    {
        if (_events is null) return null;
        try
        {
            // count: pull enough rows that the (method, path) we care about is in
            // the result set. The store sorts by hit-count desc so the cap of
            // 500 mirrors what SbWidgetBatchMiddleware uses for endpoint stats
            // and keeps the JSON snapshot small.
            var stats = await _events.GetEndpointStatsAsync(count: 500);
            var match = stats.FirstOrDefault(s =>
                string.Equals(s.Method, method, StringComparison.OrdinalIgnoreCase)
                && string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
            if (match is null) return null;
            return new SiteEndpointPerf(
                P50Ms: match.AvgProcessingTimeMs,
                P95Ms: match.P95ProcessingTimeMs,
                Samples: match.TotalCount);
        }
        catch
        {
            // Degrade silently: the perf card just renders the empty state.
            // The store's own logging handles diagnostics.
            return null;
        }
    }

    /// <summary>
    ///     Build the <see cref="PolicyScope"/> the SbPolicyStack view component
    ///     expects for a single endpoint. The legacy <c>_EndpointDetail.cshtml</c>
    ///     view uses the same shape: <c>PolicyScope.Endpoint(host, host, "{METHOD} {PATH}")</c>.
    ///     Returns null when the request has no Host header so the view can
    ///     skip the policy section rather than render a wildcard-scope badge
    ///     that would be misleading.
    /// </summary>
    private PolicyScope? TryBuildEndpointScope(string method, string path)
    {
        var host = HttpContext.Request?.Host.Host;
        if (string.IsNullOrEmpty(host)) return null;
        var pathTemplate = $"{method} {path}";
        return PolicyScope.Endpoint(host, host, pathTemplate);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
///     URL-bound filter set surfaced on the Site landing page. The page view
///     forwards these into the SbEndpointsList view component so the
///     first-paint endpoint list matches what an HTMX swap of
///     <c>/dashboard/partials/endpoints</c> with the same filter set would
///     produce.
/// </summary>
public sealed record SitePageModel(
    string? Path,
    string? Method,
    string? Threat,
    string? BotPressure,
    string BasePath);