using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

/// <summary>
///     The four Traffic side-panel breakdowns (by country, by source, top visitors,
///     threats) projected from the composed page bundle. This is the ONE projection
///     both render paths use so the two can never disagree:
///     <list type="bullet">
///         <item>The SSR first paint (<c>_Traffic.cshtml</c>) projects the stashed
///             <c>DashboardPageResult</c> through this class.</item>
///         <item>The SignalR content-ready refresh (<c>SbWidgetBatchMiddleware</c>'s
///             <c>traffic-panels</c> widget) projects the SAME slices through the SAME
///             class — the batch request reads the warm page bundle via
///             <c>TryStashWarmPageBundleAsync</c>, so an OOB swap of
///             <c>#traffic-panels</c> produces byte-identical markup to the first paint
///             (per-widget OOB refresh, same three-state warming contract).</item>
///     </list>
///     Extracted verbatim from the old inline LINQ in <c>_Traffic.cshtml</c>
///     (<c>feedback_never_two_sources_of_truth</c>); the SSR call sites still do their
///     own counters / timeseries / bot-family work, which only the chart needs.
/// </summary>
public static class TrafficPanelsProjector
{
    /// <summary>
    ///     The four panels' models, derived from the already-window-filtered visitor
    ///     projection (<paramref name="visitors"/> — see
    ///     <see cref="WidgetRenderHelpers.ProjectAsVisitors"/>, the shared post-filter
    ///     semantics both callers use) plus the bundle's country slice.
    /// </summary>
    /// <param name="visitors">Filtered visitor projection (country / bot_type / threat
    ///     filters already applied by the caller).</param>
    /// <param name="countriesData">The bundle's <c>Geo</c> slice (all countries; the
    ///     map renders the full distribution, filters do not subset it).</param>
    /// <param name="threatsOptions">Band-priority + probability-floor semantics for the
    ///     threats card (see <see cref="ThreatsFilter"/>).</param>
    /// <param name="topN">Per-card row cap (the page's <c>TrafficCardTopN</c>).</param>
    public static TrafficPanelsProjection Project(
        IReadOnlyList<ProjectedVisitor> visitors,
        IReadOnlyList<DashboardCountryStats> countriesData,
        ThreatsOptions threatsOptions,
        int topN)
    {
        // Breakdown cards. Countries come from the dedicated country-rollup slice
        // (per-detection-row aggregation, not the LFU's running-total view). Bot types +
        // visitors + threats come from the window-bounded top-bots projection.
        var countries = countriesData
            .Where(r => !string.IsNullOrEmpty(r.CountryCode))
            .OrderByDescending(r => r.TotalCount)
            .Take(topN)
            .Select(r => new CountryRow(
                CountryCode: r.CountryCode,
                Hits: r.TotalCount,
                BotShare: r.TotalCount > 0 ? r.BotCount / (double)r.TotalCount : 0d))
            .ToList();
        var botTypes = visitors
            .Where(v => v.IsBot && !string.IsNullOrEmpty(v.BotType))
            .GroupBy(v => v.BotType!)
            .Select(g => new BotTypeRow(g.Key, g.Sum(v => v.Hits)))
            .OrderByDescending(r => r.Hits)
            .Take(topN)
            .ToList();
        var topVisitors = visitors.Take(topN).ToList();
        // Threats panel inclusion is delegated to ThreatsFilter so the test in
        // Mostlylucid.BotDetection.Test/Dashboard/ThreatsFilterTests can pin the
        // band-priority + probability-floor semantics. Before Task A4 this LINQ
        // filtered to ThreatBand in {Medium, High, Critical} only, which after
        // #178 ripped the parasitic header source matched only Internal pings —
        // 11k+ classified-bot rows (Scraper / Tool / Unknown / "None" band)
        // never surfaced. ThreatsFilter now ORs in BotProbability >= floor.
        var threats = ThreatsFilter
            .Apply(visitors, threatsOptions)
            .Select(v => new ThreatRow(
                v.PrimarySignature,
                v.BotName ?? string.Empty,
                v.ThreatBand ?? "None",
                v.LastSeen))
            .ToList();

        return new TrafficPanelsProjection(countries, botTypes, topVisitors, threats);
    }
}

/// <summary>The four Traffic side-panel models, projected as one unit (see
///     <see cref="TrafficPanelsProjector"/>).</summary>
public sealed record TrafficPanelsProjection(
    IReadOnlyList<CountryRow> Countries,
    IReadOnlyList<BotTypeRow> BotTypes,
    IReadOnlyList<ProjectedVisitor> TopVisitors,
    IReadOnlyList<ThreatRow> Threats);
