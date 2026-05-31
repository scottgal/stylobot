using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Helpers;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbSummaryStatsViewComponent(
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options,
    // visitorCache is optional: remote-mode dashboard viewer hosts don't
    // register it. Per [[feedback_remote_mode_optional_di]]. When null, the
    // session-derived enrichment fields fall back to the headline summary
    // values from the event store rather than 500-ing the whole tab.
    VisitorListCache? visitorCache = null)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(string? audience = null, string? range = null)
    {
        var (startTime, endTime) = AnalyticsRangeParser.Parse(range);
        var summary = await eventStore.GetSummaryAsync(startTime, endTime, audience);
        var basePath = options.Value.BasePath.TrimEnd('/');
        var model = new SummaryStatsModel { Summary = summary, BasePath = basePath };

        if (visitorCache is null)
            return View(model);

        // Fetch all cached visitors for summary totals. The cache is bounded (default 100 entries);
        // this constant must be >= VisitorListCache._maxVisitors to ensure we get the full snapshot.
        const int maxCachedVisitors = 1_000;
        var (allVisitors, totalCount, _, _) = visitorCache.GetFiltered("all", "lastSeen", "desc", 1, maxCachedVisitors);
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

        static double AvgDuration(IReadOnlyList<CachedVisitor> visitors)
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
