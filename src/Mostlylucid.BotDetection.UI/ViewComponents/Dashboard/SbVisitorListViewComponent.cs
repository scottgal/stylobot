using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbVisitorListViewComponent(
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    // Mirrors the cap used by ServeVisitorListPartialAsync / RenderVisitorPartialAsync
    // so all three Visitors-tab entry points see the same source data slice.
    private const int MaxEntries = 500;

    public async Task<IViewComponentResult> InvokeAsync(
        string filter = "all",
        string sort = "lastSeen",
        string dir = "desc",
        int page = 1,
        int pageSize = 24)
    {
        // Read through the event store so remote-mode hosts (no in-process
        // DetectionBroadcastMiddleware feeding the local cache) still get fresh
        // data. ProjectAsVisitors mirrors ServeVisitorListPartialAsync so the
        // SSR pass and the HTMX partial swap render the same rows.
        var raw = await eventStore.GetTopBotsAsync(
            count: MaxEntries,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");
        var (items, total, counts) = WidgetRenderHelpers.ProjectAsVisitors(
            raw, filter, sort, dir, page, pageSize);

        return View(new VisitorListModel
        {
            Visitors = items,
            Counts = counts,
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