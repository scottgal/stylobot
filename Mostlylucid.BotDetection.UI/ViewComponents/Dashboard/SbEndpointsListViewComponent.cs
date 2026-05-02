using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbEndpointsListViewComponent(
    DashboardAggregateCache aggregateCache,
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string sort = "total",
        string dir = "desc",
        int page = 1,
        int pageSize = 25)
    {
        var cached = aggregateCache.Current.Endpoints;
        var data = cached.Count > 0 ? cached : await eventStore.GetEndpointStatsAsync(100);
        IEnumerable<DashboardEndpointStats> sorted = sort switch
        {
            "bots" => dir == "asc" ? data.OrderBy(x => x.BotCount) : data.OrderByDescending(x => x.BotCount),
            "botrate" => dir == "asc" ? data.OrderBy(x => x.BotRate) : data.OrderByDescending(x => x.BotRate),
            "latency" => dir == "asc" ? data.OrderBy(x => x.AvgProcessingTimeMs) : data.OrderByDescending(x => x.AvgProcessingTimeMs),
            "unique" => dir == "asc" ? data.OrderBy(x => x.UniqueSignatures) : data.OrderByDescending(x => x.UniqueSignatures),
            _ => dir == "asc" ? data.OrderBy(x => x.TotalCount) : data.OrderByDescending(x => x.TotalCount)
        };
        return View(new EndpointsListModel
        {
            Endpoints = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            SortField = sort,
            SortDir = dir,
            Page = page,
            PageSize = pageSize,
            TotalCount = data.Count,
            BasePath = options.Value.BasePath.TrimEnd('/')
        });
    }
}
