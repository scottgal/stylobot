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
        CancellationToken ct)
    {
        // The view consults Layout.BasePath for HTMX urls inside the partial via
        // Model.BasePath. We populate that the same way the middleware does so the
        // chips + pills produce the same URLs as an HTMX swap.
        var basePath = _layout.Value.V2Enabled
            ? "/dashboard"
            : "/dashboard";

        // Fetch authoritative segment counts from the store (single source for summary + tabs)
        var now = DateTime.UtcNow;
        var start = now.AddHours(-24);

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
            var manifest = _manifests.For("dashboard.visitors")
                ?? _manifests.For("dashboard.traffic");
            if (manifest != null)
            {
                var pageWindow = new DashboardPageWindow(
                    StartTime: start,
                    EndTime: now,
                    AudienceFilter: "all",
                    ProbMin: null,
                    Domains: null,
                    TopN: 500,
                    BucketMinutes: 60);
                var page = await _contentCache.GetCurrentAsync(manifest, pageWindow, ct);
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
            Countries: countriesData ?? new List<DashboardCountryStats>());

        return View("/Views/StyloBot/Dashboard/Visitors/Index.cshtml", model);
    }

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
    IReadOnlyList<DashboardCountryStats>? Countries = null);