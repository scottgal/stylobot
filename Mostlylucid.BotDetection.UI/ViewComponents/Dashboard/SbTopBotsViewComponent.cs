using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
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
        string widgetId = "topbots")
    {
        var allBots = signatureCache.GetTopBots(page: 1, pageSize: signatureCache.MaxEntries, sortBy: sortBy, sortDir: sortDir, filter: filter);
        var pagedBots = allBots.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return View(new TopBotsListModel
        {
            Bots = pagedBots,
            Page = page,
            PageSize = pageSize,
            TotalCount = allBots.Count,
            SortField = sortBy,
            SortDir = sortDir,
            BasePath = options.Value.BasePath.TrimEnd('/'),
            Filter = filter,
            WidgetId = widgetId
        });
    }
}
