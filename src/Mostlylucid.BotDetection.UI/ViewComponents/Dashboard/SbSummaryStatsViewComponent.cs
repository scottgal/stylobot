using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Helpers;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

[DashboardWidget("summary", DatasetKind.SummaryStats)]
public class SbSummaryStatsViewComponent(
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options,
    // signatureCache is optional: remote-mode dashboard viewer hosts don't
    // register it. Per [[feedback_remote_mode_optional_di]]. When null, the
    // session-derived enrichment fields fall back to the headline summary
    // values from the event store rather than 500-ing the whole tab.
    SignatureAggregateCache? signatureCache = null,
    // aggregateCache is optional too: the unwindowed broadcaster snapshot used
    // as the first-load fallback when the windowed summary is still cold.
    DashboardAggregateCache? aggregateCache = null)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string? audience = null, string? range = null)
    {
        var (startTime, endTime) = AnalyticsRangeParser.Parse(range);

        // If the page composer stashed a DashboardPageResult, read the Summary slice from it
        // directly (zero store calls). Otherwise fall through to the existing self-fetch so
        // VCs rendered on non-composer pages still work.
        var pageResult = HttpContext?.Items["sb.dashboard.pageresult"] as DashboardPageResult;

        // A genuine cold miss -- render the warming placeholder instead of falling through
        // to a live store call. See SbTopBotsViewComponent for the same guard's rationale.
        if (pageResult is { IsWarming: true })
        {
            var warmingSummary = new DashboardSummary
            {
                Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0,
                UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(),
                UniqueSignatures = 0,
            };
            return View(new SummaryStatsModel
            {
                Summary = warmingSummary,
                BasePath = options.Value.BasePath.TrimEnd('/'),
                IsWarming = true,
            });
        }

        var scopedDomains = HttpContext?.RequestServices?
            .GetService<IDashboardDomainScope>()
            ?.GetSelectedDomains(HttpContext);
        var summary = pageResult?.Summary ?? await eventStore.GetSummaryAsync(startTime, endTime, audience, domains: scopedDomains);

        // First-load resilience (operator audit 2026-08-12): the SWR-wrapped windowed
        // summary can return the empty placeholder on a cold key — a silent "0 req"
        // strip beside populated widgets (Top Bots 390). Fall back to the aggregate
        // cache's unwindowed LIVE snapshot (the broadcaster's) rather than painting
        // zeros; the windowed read warms in the background and the next paint is exact.
        if (summary.TotalRequests == 0 && aggregateCache?.Current.Summary is { TotalRequests: > 0 } liveSummary)
            summary = liveSummary;

        var basePath = options.Value.BasePath.TrimEnd('/');
        var model = new SummaryStatsModel { Summary = summary, BasePath = basePath };

        if (signatureCache is null)
            return View(model);

        // Fetch all cached visitors for summary totals. The cache's MaxEntries cap
        // (200 by default) bounds the snapshot; passing a larger pageSize just
        // returns everything in one page.
        const int maxProjectedVisitors = 1_000;
        var (allVisitors, totalCount, _, _) = signatureCache.GetFiltered("all", "lastSeen", "desc", 1, maxProjectedVisitors);

        // FUNDAMENTAL FIX (operator directive 2026-08-12): the site page's summary
        // ALWAYS painted zero visitor stats — the remote dashboard host's
        // SignatureAggregateCache is empty until the gateway beacon warms it, and an
        // EMPTY (non-null) cache took the enrichment path below, producing permanent
        // zeros where the traffic page showed real numbers. When the cache holds no
        // visitors, fall back to the headline summary values (real unique
        // signatures / bot / human fingerprints from the store) instead of zeros.
        if (allVisitors.Count == 0 && signatureCache.Count == 0)
        {
            model.UniqueVisitors = summary.UniqueSignatures;
            model.BotSessions = summary.BotFingerprints;
            model.HumanSessions = summary.HumanFingerprints;
            return View(model);
        }

        var humanVisitors = allVisitors.Where(v => !v.IsBot).ToList();
        var botVisitors = allVisitors.Where(v => v.IsBot).ToList();

        model.UniqueVisitors = totalCount;
        model.ActiveSessions = allVisitors.Count(v => v.LastSeen > DateTime.UtcNow.AddMinutes(-5));
        model.BotSessions = botVisitors.Count;
        model.HumanSessions = humanVisitors.Count;

        var totalWithHits = allVisitors.Count(v => v.Hits > 0);
        model.BounceRate = totalWithHits > 0
            ? Math.Round((double)allVisitors.Count(v => v.Hits == 1) / totalWithHits * 100, 1) : 0;
        model.HumanBounceRate = humanVisitors.Count > 0
            ? Math.Round((double)humanVisitors.Count(v => v.Hits == 1) / humanVisitors.Count * 100, 1) : 0;
        model.BotBounceRate = botVisitors.Count > 0
            ? Math.Round((double)botVisitors.Count(v => v.Hits == 1) / botVisitors.Count * 100, 1) : 0;

        static double AvgDuration(IReadOnlyList<ProjectedVisitor> visitors)
        {
            var withDuration = visitors.Where(v => v.Hits > 1).ToList();
            if (withDuration.Count == 0) return 0;
            return Math.Round(withDuration.Average(v => (v.LastSeen - v.FirstSeen).TotalSeconds), 1);
        }

        model.AvgSessionDurationSecs = AvgDuration(allVisitors);
        model.HumanAvgSessionDurationSecs = AvgDuration(humanVisitors);
        model.BotAvgSessionDurationSecs = AvgDuration(botVisitors);

        return View(model);
    }
}
