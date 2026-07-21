using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard;
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
/// </summary>
[Route("dashboard/visitors")]
public sealed class VisitorsController : Controller
{
    private readonly IDashboardEventStore _eventStore;
    private readonly IOptions<DashboardLayoutOptions> _layout;

    public VisitorsController(
        IDashboardEventStore eventStore,
        IOptions<DashboardLayoutOptions> layout)
    {
        _eventStore = eventStore;
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

        // Fetch summary data for accurate counters (same as Traffic page)
        var now = DateTime.UtcNow;
        var start = now.AddHours(-24);

        DashboardSummary? summary = null;
        List<DashboardTopBotEntry> topBots = new();

        try
        {
            summary = await _eventStore.GetSummaryAsync(startTime: start, endTime: now, domains: null);
            topBots = await _eventStore.GetTopBotsAsync(
                startTime: start, endTime: now, limit: 500,
                ProbMin: null, Domains: null, TopN: 500, BucketMinutes: 1440);
        }
        catch
        {
            // Graceful degradation: show page without counters if event store fails
        }

        // Build counters using the same logic as TrafficController
        var counters = BuildCounters(summary, topBots);

        var model = new VisitorsPageModel(
            Filter: string.IsNullOrWhiteSpace(filter) ? "all" : filter,
            Country: NullIfEmpty(country),
            BotType: NullIfEmpty(botType),
            Threat: NullIfEmpty(threat),
            FingerprintId: NullIfEmpty(fingerprint),
            Internal: @internal,
            BasePath: basePath,
            Counters: counters);

        return View("/Views/StyloBot/Dashboard/Visitors/Index.cshtml", model);
    }

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static TrafficCounters BuildCounters(
        DashboardSummary? current,
        IReadOnlyList<DashboardTopBotEntry> currentBots)
    {
        var (curTotal, curHumans, curBots) = current is null
            ? SumHits(currentBots)
            : (current.TotalRequests, current.HumanRequests, current.BotRequests);

        return new TrafficCounters(
            Total: curTotal,
            Humans: curHumans,
            Bots: curBots,
            BotShare: curBots / (double)Math.Max(1, curTotal),
            TotalDelta: 0,
            HumansDelta: 0,
            BotsDelta: 0,
            TotalDeltaPct: 0,
            HumansDeltaPct: 0,
            BotsDeltaPct: 0);
    }

    private static (int Total, int Humans, int Bots) SumHits(IReadOnlyList<DashboardTopBotEntry> rows)
    {
        var total = 0; var humans = 0; var bots = 0;
        foreach (var v in rows)
        {
            total += v.HitCount;
            if (v.BotProbability >= 0.8) bots += v.HitCount;
            else if (v.BotProbability < 0.3) humans += v.HitCount;
        }
        return (total, humans, bots);
    }
}

/// <summary>
///     URL-bound filter set surfaced on the Visitors landing page. The page view
///     wires these into an HTMX <c>hx-get</c> against
///     <c>/dashboard/partials/visitors</c> so the first-paint and subsequent
///     swaps share the same filter shape. Counters provide accurate visitor
///     summary matching the Traffic page numbers.
/// </summary>
public sealed record VisitorsPageModel(
    string Filter,
    string? Country,
    string? BotType,
    string? Threat,
    string? FingerprintId,
    bool Internal,
    string BasePath,
    TrafficCounters? Counters = null);