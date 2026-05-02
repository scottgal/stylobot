using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbCountriesListViewComponent(
    DashboardAggregateCache aggregateCache,
    IDashboardEventStore eventStore,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string sort = "total",
        string dir = "desc",
        int page = 1,
        int pageSize = 20)
    {
        var cached = aggregateCache.Current.Countries;
        var data = cached.Count > 0 ? cached : await eventStore.GetCountryStatsAsync(100);
        IEnumerable<DashboardCountryStats> sorted = sort switch
        {
            "bots" => dir == "asc" ? data.OrderBy(x => x.BotCount) : data.OrderByDescending(x => x.BotCount),
            "botrate" => dir == "asc" ? data.OrderBy(x => x.BotRate) : data.OrderByDescending(x => x.BotRate),
            "humans" => dir == "asc" ? data.OrderBy(x => x.HumanCount) : data.OrderByDescending(x => x.HumanCount),
            _ => dir == "asc" ? data.OrderBy(x => x.TotalCount) : data.OrderByDescending(x => x.TotalCount)
        };
        return View(new CountriesListModel
        {
            Countries = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            SortField = sort,
            SortDir = dir,
            Page = page,
            PageSize = pageSize,
            TotalCount = data.Count,
            BasePath = options.Value.BasePath.TrimEnd('/')
        });
    }
}
