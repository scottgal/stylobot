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

        List<DashboardTopBotEntry> allBots;
        if (!string.IsNullOrEmpty(audience) || startTime.HasValue)
        {
            // Parameter-driven: bypass the signature cache, query the store directly.
            allBots = await eventStore.GetTopBotsAsync(
                count: signatureCache.MaxEntries,
                startTime: startTime,
                endTime: endTime,
                audienceFilter: audience);
        }
        else
        {
            // Legacy: cache-first — the write-through SignatureAggregateCache is the source of
            // truth for the live top-bots widget. Same collapse the OOB refresh path applies
            // (BuildTopBotsModel in both StyloBotDashboardMiddleware + SbWidgetBatchMiddleware).
            // Without the collapse, SSR paints a raw 100-row Amazonbot list and the very next
            // OOB swap drops to one row, producing the "loads long, instantly shortens" flicker.
            allBots = signatureCache.GetTopBots(
                page: 1, pageSize: signatureCache.MaxEntries,
                sortBy: sortBy, sortDir: sortDir, filter: filter);
        }

        // Collapse groupable identities and apply search filter on both paths.
        var grouped = WidgetRenderHelpers.CollapseGroupableIdentities(allBots);
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
            Counts = signatureCache.GetCounts(),
            Query = string.IsNullOrWhiteSpace(q) ? null : q.Trim(),
        });
    }
}
