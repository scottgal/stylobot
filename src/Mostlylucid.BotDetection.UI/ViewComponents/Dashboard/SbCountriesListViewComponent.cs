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

[DashboardWidget("countries", DatasetKind.GeoBreakdown)]
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
        int pageSize = 20,
        string? audience = null,
        string? range = null)
    {
        var (startTime, endTime) = AnalyticsRangeParser.Parse(range);

        // If the page composer stashed a DashboardPageResult, read the Geo slice from it
        // directly (zero store calls). Otherwise fall through to the existing cache-first /
        // parameter-driven self-fetch so VCs rendered on non-composer pages still work.
        var pageResult = HttpContext?.Items["sb.dashboard.pageresult"] as DashboardPageResult;

        // Dashboard-wide domain scope (DI seam). FOSS default returns null (no filter =
        // today's behavior); a commercial impl supplies the operator's selected domains,
        // threaded into the self-fetch store reads below. The composed path is already
        // scoped upstream via the page window (BuildVisitorsPageWindow).
        var scopedDomains = HttpContext?.RequestServices?
            .GetService<IDashboardDomainScope>()
            ?.GetSelectedDomains(HttpContext);

        // A genuine cold miss -- render the warming placeholder instead of falling through
        // to a live store call. See SbTopBotsViewComponent for the same guard's rationale.
        if (pageResult is { IsWarming: true })
        {
            return View(new CountriesListModel
            {
                Countries = [], SortField = sort, SortDir = dir, Page = page, PageSize = pageSize,
                TotalCount = 0, BasePath = options.Value.BasePath.TrimEnd('/'), IsWarming = true,
            });
        }

        IReadOnlyList<DashboardCountryStats> data;
        if (pageResult?.Geo is { } composedGeo)
        {
            data = composedGeo;
        }
        else if (!string.IsNullOrEmpty(audience) || startTime.HasValue)
        {
            // Parameter-driven: bypass cache, query store with the provided args.
            data = await eventStore.GetCountryStatsAsync(500, startTime, endTime, audience, scopedDomains);
        }
        else
        {
            // Legacy: cache-first / store-fallback so the live dashboard hot path is unchanged.
            var cached = aggregateCache.Current.Countries;
            data = cached.Count > 0 ? cached : await eventStore.GetCountryStatsAsync(500, domains: scopedDomains);
        }
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
