using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Helpers;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbTopBotsViewComponent(
    SignatureAggregateCache signatureCache,
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        int page = 1,
        int pageSize = 10,
        string sortBy = "default",
        string sortDir = "desc",
        string filter = "bots",
        string widgetId = "topbots",
        string? q = null,
        string? audience = null,
        string? range = null)
    {
        var (startTime, endTime) = AnalyticsRangeParser.Parse(range);

        // Always read through the event store -- the in-process SignatureAggregateCache
        // is fresh only on a gateway host (write-through via DetectionBroadcastMiddleware);
        // on a remote-mode dashboard host it pins to startup-warm values and the widget
        // shows stale rows forever. Same approach taken in StyloBotDashboardMiddleware.BuildTopBotsModel
        // and SbWidgetBatchMiddleware.BuildTopBotsModel. Fetching audience=all here gives us
        // a cross-cutting top-N so the widget header (All / Bots / Humans) reflects reality
        // and the audience switcher can filter client-side.
        var rangeStart = startTime ?? DateTime.UtcNow.AddHours(-24);
        var rangeEnd   = endTime   ?? DateTime.UtcNow;
        var fetchAudience = string.IsNullOrEmpty(audience) ? "all" : audience;
        var raw = await eventStore.GetTopBotsAsync(
            count: signatureCache.MaxEntries,
            startTime: rangeStart,
            endTime: rangeEnd,
            audienceFilter: fetchAudience);
        var bots = raw.Count(b => b.IsKnownBot);
        var humans = raw.Count - bots;

        IEnumerable<DashboardTopBotEntry> filtered = filter switch
        {
            "bots"   => raw.Where(b => b.IsKnownBot),
            "humans" => raw.Where(b => !b.IsKnownBot),
            _        => raw
        };
        var sorted = WidgetRenderHelpers.SortTopBots(filtered, sortBy, sortDir).ToList();
        var grouped = WidgetRenderHelpers.CollapseGroupableIdentities(sorted);
        grouped = WidgetRenderHelpers.ApplySearchFilter(grouped, q);
        var pagedBots = grouped.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return View(new TopBotsListModel
        {
            Bots = pagedBots,
            Page = page,
            PageSize = pageSize,
            TotalCount = grouped.Count,
            SortField = sortBy,
            SortDir = sortDir,
            BasePath = options.Value.BasePath.TrimEnd('/'),
            Filter = filter,
            WidgetId = widgetId,
            Counts = new TopBotsCounts(All: raw.Count, Bots: bots, Humans: humans),
            Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
        });
    }
}
