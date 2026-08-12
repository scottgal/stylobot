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

    public VisitorsController(
        IDashboardEventStore eventStore,
        IDashboardContentCache contentCache,
        IDashboardPageManifestSource manifests,
        IOptions<DashboardLayoutOptions> layout)
    {
        _eventStore = eventStore;
        _contentCache = contentCache;
        _manifests = manifests;
        _layout = layout;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        [FromQuery] string? filter,
        [FromQuery] string? country,
        [FromQuery(Name = "bot_type")] string? botType,
        [FromQuery] string? threat,
        [FromQuery] string? fingerprint,
        [FromQuery(Name = "internal")] bool @internal,
        [FromQuery(Name = "window")] string? window,
        CancellationToken ct)
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
        var pageWindow = DashboardRoutingHelpers.BuildPinnedWindow(windowToken, now);
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
            Internal: @internal,
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
