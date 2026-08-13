using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>
///     Owns the Site IA group. Composes a minimal page bundle (summary + endpoints)
///     so self-fetching view components (<see cref="SbSummaryStatsViewComponent"/>,
///     <see cref="SbSiteHealthViewComponent"/>, <see cref="SbEndpointsListViewComponent"/>)
///     find a warm slice in <see cref="HttpContext.Items"/> instead of cold-fetching
///     from the event store on every render. Mirrors TrafficController's
///     compose-then-render pattern so VCs on both pages share the same warm-read path.
/// </summary>
[Route("dashboard/site")]
public sealed class SiteController : Controller
{
    private readonly IOptions<DashboardLayoutOptions> _layout;
    private readonly IOptions<StyloBotDashboardOptions> _dashOpts;
    private readonly IDashboardEventStore _eventStore;
    private readonly IDashboardContentCache _contentCache;
    private readonly IDashboardPageManifestSource _manifests;
    private readonly IOptions<DashboardMaterializerOptions> _materializerOpts;

    public SiteController(
        IOptions<DashboardLayoutOptions> layout,
        IOptions<StyloBotDashboardOptions> dashOpts,
        IDashboardEventStore eventStore,
        IDashboardContentCache contentCache,
        IDashboardPageManifestSource manifests,
        IOptions<DashboardMaterializerOptions>? materializerOpts = null)
    {
        _layout = layout;
        _dashOpts = dashOpts;
        _eventStore = eventStore;
        _contentCache = contentCache;
        _manifests = manifests;
        _materializerOpts = materializerOpts ?? Microsoft.Extensions.Options.Options.Create(new DashboardMaterializerOptions());
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] string? path,
        [FromQuery] string? method,
        [FromQuery] string? threat,
        [FromQuery(Name = "bot_pressure")] string? botPressure,
        [FromQuery(Name = "window")] string? window,
        [FromQuery] int? partial,
        CancellationToken ct)
    {
        var basePath = _layout.Value.V2Enabled
            ? "/dashboard"
            : "/dashboard";

        var now = DateTime.UtcNow;
        var domainsForQuery = ReadDomainFilter();
        // The window MUST be derived through the same pinned-prewarm builder the tick
        // materializer uses (DashboardRoutingHelpers.BuildPinnedWindow) or the content-cache
        // envelope key never matches the prewarmed bundle — the site page then cold-misses
        // every load and the summary strip paints placeholder zeros (the 2026-08-12 P0).
        // Only the tokens the pinned prewarm covers resolve; anything else falls back to 24h.
        var windowToken = NormalizeWindowToken(window);

        // Compose a minimal page bundle so VCs read warm data instead of cold-fetching.
        // The Traffic page does the same via DashboardContentCache; the Site page uses a
        // smaller manifest (only "summary" + "endpoints" — the two datasets it renders).
        // Degradations comes from a different path (GetDegradationHistoryAsync), not the
        // cached compose, so SbSiteHealth self-fetches regardless.
        var manifest = _manifests.For("dashboard.site")
                       ?? new DashboardPageManifest("dashboard.site",
                           new[] { "summary", "site-health", "endpoints" });
        var pageWindow = DashboardRoutingHelpers.BuildPinnedWindow(windowToken, now, domainsForQuery);

        DashboardPageResult? page = null;
        try { page = await _contentCache.GetCurrentAsync(manifest, pageWindow, ct); }
        catch { /* content cache miss — VCs self-fetch */ }

        // First-paint bounded wait (operator directive 2026-08-12: pages NEVER load with
        // empty data). A first load racing the materializer (fresh pod, post-deploy) sees a
        // Warming envelope for the PINNED default view — hold the paint and re-read until
        // the stash lands, bounded by FirstPaintStashWaitMs. The read never composes (the
        // materializer warms on its own schedule); this only waits for its output. Custom
        // filters (non-pinned tokens, domain selections) never wait — they keep the
        // sanctioned spinner + SignalR fill path.
        if (page is { IsWarming: true }
            && domainsForQuery is null
            && _materializerOpts.Value.PrewarmWindows.Contains(windowToken)
            && _materializerOpts.Value.FirstPaintStashWaitMs > 0)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(_materializerOpts.Value.FirstPaintStashWaitMs);
            while (page is { IsWarming: true } && DateTime.UtcNow < deadline)
            {
                await Task.Delay(Math.Max(10, _materializerOpts.Value.FirstPaintStashPollMs), ct);
                try { page = await _contentCache.GetCurrentAsync(manifest, pageWindow, ct); }
                catch { break; }
            }
        }
        // Cached-poison guard (operator directive 2026-08-12): never stash a Warming
        // or incomplete bundle as authoritative — the render helpers treat a present
        // stash as real data, so stashing an empty-summary bundle would paint "0 req"
        // and (via the batch shingles) cache it. Same completeness shape the dashboard
        // middleware uses (IsPageBundleCompleteEnoughToStash); a miss leaves the VCs
        // on their self-fetch paths against live data.
        if (page is { IsWarming: false }
            && Middleware.StyloBotDashboardMiddleware.IsPageBundleCompleteEnoughToStash(page, manifest))
            HttpContext.Items["sb.dashboard.pageresult"] = page;

        // Seed the endpoints first-paint reader so SbEndpointsList renders real rows
        // on first paint — same pattern TrafficController uses.
        var endpointsData = (page.Endpoints ?? new List<DashboardEndpointStats>()).ToList();
        if (endpointsData.Count == 0)
        {
            try
            {
                endpointsData = await _eventStore.GetEndpointStatsAsync(
                    count: 500, startTime: pageWindow.StartTime, endTime: now, domains: domainsForQuery);
            }
            catch { /* degrade to whatever the composed slice had */ }
        }
        DashboardEndpointsFirstPaintContext.Set(HttpContext,
            new SsrEndpointsFirstPaintReader(endpointsData));

        var model = new SitePageModel(
            Path: NullIfEmpty(path),
            Method: NullIfEmpty(method),
            Threat: NullIfEmpty(threat),
            BotPressure: NullIfEmpty(botPressure),
            BasePath: basePath,
            Window: NullIfEmpty(window));

        // Period-selector swap path (rebuild 2026-08-12, retargeted 2026-08-13): the
        // site page's content region refetches this endpoint after a scope-bar period
        // click. The FOSS Site/Index.cshtml IS the swappable region (content-only
        // section with #sb-site-body) — the swap renders the same view; the retired
        // commercial _Body overlay was shadowed by the FOSS view anyway. The swap URL
        // carries NO window (DashboardScopeBaselineMiddleware applies the cookie's).
        if (partial == 1)
            return PartialView("/Views/StyloBot/Dashboard/Site/Index.cshtml", model);
        if (Request.Headers.ContainsKey("HX-Request"))
            return PartialView("/Views/StyloBot/Dashboard/Site/Index.cshtml", model);
        return View("/Views/StyloBot/Dashboard/Site/Index.cshtml", model);
    }

    /// <summary>
    ///     Redirect the old query-string endpoint URL to the real canonical
    ///     detail page at /dashboard/endpoint/{method}/{path}.
    /// </summary>
    [HttpGet("endpoint")]
    public IActionResult EndpointDetail(
        [FromQuery] string? path,
        [FromQuery] string? method)
    {
        if (string.IsNullOrWhiteSpace(path)) return RedirectToAction(nameof(Index));
        var resolvedMethod = string.IsNullOrWhiteSpace(method) ? "GET" : method.ToUpperInvariant();
        return Redirect($"/dashboard/endpoint/{resolvedMethod}/{Uri.EscapeDataString(path)}");
    }

    private IReadOnlyList<string>? ReadDomainFilter()
    {
        var values = Request.Query["domain"]
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return values.Length > 0 ? values : null;
    }

    /// <summary>
    ///     Resolves the query window token against the pinned-prewarm token set. Only the
    ///     tokens the materializer's Tier 1 prewarm covers pass through; null / unknown /
    ///     custom fall back to the 24h default so the page's envelope key matches what the
    ///     prewarm warmed. The resolved token feeds
    ///     <see cref="DashboardRoutingHelpers.BuildPinnedWindow"/> — never a hand-rolled
    ///     window here (the two derivations drifted once and the site summary painted
    ///     zeros permanently, 2026-08-12).
    /// </summary>
    private static string NormalizeWindowToken(string? window) => window switch
    {
        "6h" or "12h" or "24h" or "7d" or "30d" => window,
        _ => "24h",
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Bounded first-paint reader for the endpoints widget.</summary>
    private sealed class SsrEndpointsFirstPaintReader : IDashboardEndpointsFirstPaintReader
    {
        private readonly List<DashboardEndpointStats> _data;
        public SsrEndpointsFirstPaintReader(List<DashboardEndpointStats> data) => _data = data;
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count, DateTime? startTime, DateTime? endTime, string? audienceFilter,
            IReadOnlyList<string>? domains, CancellationToken cancellationToken = default)
            => Task.FromResult(_data);
    }
}

/// <summary>
///     URL-bound filter set surfaced on the Site landing page.
/// </summary>
public sealed record SitePageModel(
    string? Path,
    string? Method,
    string? Threat,
    string? BotPressure,
    string BasePath,
    string? Window = null);
