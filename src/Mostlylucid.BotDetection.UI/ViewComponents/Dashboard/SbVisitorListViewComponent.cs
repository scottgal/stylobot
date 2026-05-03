using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbVisitorListViewComponent(VisitorListCache cache, IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public IViewComponentResult Invoke(
        string filter = "all",
        string sort = "lastSeen",
        string dir = "desc",
        int page = 1,
        int pageSize = 24)
    {
        var (items, total, _, _) = cache.GetFiltered(filter, sort, dir, page, pageSize);
        return View(new VisitorListModel
        {
            Visitors = items,
            Counts = cache.GetCounts(),
            Filter = filter,
            SortField = sort,
            SortDir = dir,
            Page = page,
            PageSize = pageSize,
            TotalCount = total,
            BasePath = options.Value.BasePath.TrimEnd('/')
        });
    }
}
