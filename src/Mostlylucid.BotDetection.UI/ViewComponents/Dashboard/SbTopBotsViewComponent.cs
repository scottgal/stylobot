using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbTopBotsViewComponent(
    SignatureAggregateCache signatureCache,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public IViewComponentResult Invoke(
        int page = 1,
        int pageSize = 10,
        string sortBy = "default",
        string sortDir = "desc",
        string filter = "bots",
        string widgetId = "topbots",
        string? q = null)
    {
        var allBots = signatureCache.GetTopBots(page: 1, pageSize: signatureCache.MaxEntries, sortBy: sortBy, sortDir: sortDir, filter: filter);
        // Same collapse the OOB refresh path applies (BuildTopBotsModel in both
        // StyloBotDashboardMiddleware + SbWidgetBatchMiddleware). Without it the
        // SSR painted a raw 100-row Amazonbot list and the very next OOB swap
        // dropped to one row, producing the "loads long, instantly shortens"
        // flicker the user has flagged twice. SSR + OOB must agree on row count.
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
