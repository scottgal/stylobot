using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Controllers;

/// <summary>
///     V1 (dashboard IA collapse plan): renders the Visitors landing page that
///     the V2 sidebar (Group 1) links to at <c>/dashboard/visitors</c>. The
///     filter pills + visitor table live in the existing
///     <c>SbVisitorList/Default.cshtml</c> partial — this controller binds the
///     URL filters (<c>?country=</c>, <c>?bot_type=</c>, <c>?threat=</c>,
///     <c>?fingerprint=</c>, <c>?internal=true</c>) into the model so the
///     SSR-first page renders with the active filter chips + Internal pill in
///     the same shape an HTMX swap of the same partial would produce.
///
///     The Visitors layout includes a map and signature patterns (reused from Traffic).
/// </summary>
[Route("dashboard/visitors")]
public sealed class VisitorsController : Controller
{
    private readonly IDashboardEventStore _eventStore;
    private readonly IDashboardContentCache _contentCache;
    private readonly IDashboardPageManifestSource _manifests;
    private readonly IOptions<DashboardLayoutOptions> _layout;
    private readonly IOptions<DashboardMaterializerOptions> _materializerOpts;

    public VisitorsController(
        IDashboardEventStore eventStore,
        IDashboardContentCache contentCache,
        IDashboardPageManifestSource manifests,
        IOptions<DashboardLayoutOptions> layout,
        IOptions<DashboardMaterializerOptions>? materializerOpts = null)
    {
        _eventStore = eventStore;
        _contentCache = contentCache;
        _manifests = manifests;
        _layout = layout;
        _materializerOpts = materializerOpts ?? Microsoft.Extensions.Options.Options.Create(new DashboardMaterializerOptions());
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] string? filter,
        [FromQuery] string? country,
        [FromQuery(Name = "bot_type")] string? botType,
        [FromQuery] string? threat,
        [FromQuery] string? fingerprint,
        [FromQuery(Name = "window")] string? window,
        [FromQuery(Name = "internal")] string? _internal = null,
        CancellationToken ct = default)
    {
        // The view consults Layout.BasePath for HTMX urls inside the partial via
        // Model.BasePath. We populate that the same way the middleware does so the
        // chips + pills produce the same URLs as an HTMX swap.
        var basePath = _layout.Value.V2Enabled
            ? "/dashboard"
            : "/dashboard";

        // Period-selector chain (rebuild 2026-08-12): the visitors page MUST honour the
        // scope bar's window — the baseline middleware applies the cookie as ?window=
        // when the swap URL carries none. Only the pinned tokens resolve; anything else
        // falls back to 24h (the same normalisation SiteController uses).
        var now = DateTime.UtcNow;
        var windowToken = NormalizeWindowToken(window);
        // Internal-traffic toggle (operator 2026-08-19): the top-level bar toggle
        // sends ?internal=show (the baseline middleware applies the cookie); the
        // visitor list's own Internal pill has historically used ?internal=true —
        // accept both vocabularies so the two controls agree on the same flag.
        var showInternal = string.Equals(_internal, "show", StringComparison.OrdinalIgnoreCase)
            || string.Equals(_internal, "true", StringComparison.OrdinalIgnoreCase);
        var pageWindow = DashboardRoutingHelpers.BuildPinnedWindow(
            windowToken, now, audienceFilter: showInternal ? "all_incl_internal" : "all");
        var start = pageWindow.StartTime!.Value;

        // Visitors and Traffic must consume the same composed page bundle. In
        // remote mode the bundle is the live gateway read-through; asking the
        // view component to open a second direct store path can legitimately
        // return an empty fallback while Traffic is populated.
        DashboardPageResult? page = null;
        try
        {
            var manifest = _manifests.For("dashboard.traffic");
            if (manifest is not null)
            {
                page = await _contentCache.GetCurrentAsync(manifest, pageWindow, ct);

                // First-paint bounded wait (operator directive 2026-08-12: pages NEVER
                // load with empty data — the visitors page's counters strip has no
                // warming branch, so a cold stash painted zeros beside real widgets).
                // Same shape as SiteController/TrafficController: PINNED default views
                // only, bounded by FirstPaintStashWaitMs; the read never composes.
                if (page is { IsWarming: true }
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
        // Terminal state (operator directive 2026-08-13): the bounded wait gave up —
        // the data feed stayed unavailable (gateway compose flap). Stamp the degraded
        // marker so the widget renders the explicit "data feed unavailable" state
        // instead of an infinite spinner; the next tick/beacon retries.
        if (page is { IsWarming: true })
            DashboardWarmingSignal.MarkDegraded(HttpContext);
                }

                HttpContext.Items["sb.dashboard.pageresult"] = page;
            }
        }
        catch
        {
            // The event-store fallback below remains available on a cold/error path.
        }

        TrafficCounters? counters = null;
        IReadOnlyList<DashboardCountryStats>? countriesData = null;

        try
        {
            var segmentCounts = await _eventStore.GetVisitorSegmentCountsAsync(
                start, now, country: country, botType: botType, threat: threat);

            // Convert segment counts to TrafficCounters (same format as summary row)
            counters = new TrafficCounters(
                Total: segmentCounts.All,
                Humans: segmentCounts.Humans,
                Bots: segmentCounts.Bots,
                BotShare: segmentCounts.All > 0 ? segmentCounts.Bots / (double)segmentCounts.All : 0,
                TotalDelta: 0,
                HumansDelta: 0,
                BotsDelta: 0,
                TotalDeltaPct: 0,
                HumansDeltaPct: 0,
                BotsDeltaPct: 0);
        }
        catch
        {
            // Graceful degradation: show page without counters if event store fails
        }

        // Fetch countries data for the map (reused from Traffic page)
        try
        {
            if (page is not null)
            {
                countriesData = (IReadOnlyList<DashboardCountryStats>)(page.Geo ?? new List<DashboardCountryStats>());
            }
        }
        catch
        {
            // Graceful degradation: show page without countries map if cache fails
        }

        var model = new VisitorsPageModel(
            Filter: string.IsNullOrWhiteSpace(filter) ? "all" : filter,
            Country: NullIfEmpty(country),
            BotType: NullIfEmpty(botType),
            Threat: NullIfEmpty(threat),
            FingerprintId: NullIfEmpty(fingerprint),
            Internal: showInternal,
            BasePath: basePath,
            Counters: counters,
            Countries: countriesData ?? new List<DashboardCountryStats>(),
            Window: windowToken);

        return View("/Views/StyloBot/Dashboard/Visitors/Index.cshtml", model);
    }

    /// <summary>
    ///     Period-selector tokens the visitors page resolves (the scope bar's pinned set);
    ///     anything else falls back to the 24h default so the read always keys a pinned
    ///     prewarm envelope.
    /// </summary>
    private static string NormalizeWindowToken(string? window) => window switch
    {
        "6h" or "12h" or "24h" or "7d" or "30d" => window,
        _ => "24h",
    };

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;
}

/// <summary>
///     URL-bound filter set surfaced on the Visitors landing page. The page view
///     wires these into an HTMX <c>hx-get</c> against
///     <c>/dashboard/partials/visitors</c> so the first-paint and subsequent
///     swaps share the same filter shape. Counters provide accurate visitor
///     summary matching the Traffic page numbers. Countries data powers the
///     world map in the right-column sidebar.
/// </summary>
public sealed record VisitorsPageModel(
    string Filter,
    string? Country,
    string? BotType,
    string? Threat,
    string? FingerprintId,
    bool Internal,
    string BasePath,
    TrafficCounters? Counters = null,
    IReadOnlyList<DashboardCountryStats>? Countries = null,
    string? Window = null);
