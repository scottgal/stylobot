using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Helpers;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbUserAgentsListViewComponent(
    DashboardAggregateCache aggregateCache,
    DashboardUserAgentAggregator userAgentAggregator,
    IOptions<StyloBotDashboardOptions> options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(
        string filter = "all",
        string sort = "requests",
        string dir = "desc",
        int page = 1,
        int pageSize = 25,
        string? audience = null,
        string? range = null)
    {
        var (startTime, endTime) = AnalyticsRangeParser.Parse(range);

        List<DashboardUserAgentSummary> all;
        if (!string.IsNullOrEmpty(audience) || startTime.HasValue)
        {
            // Parameter-driven: bypass cache, aggregate directly from the store.
            all = await userAgentAggregator.ComputeAsync(audience, startTime, endTime);
        }
        else
        {
            // Legacy: use the pre-computed cache snapshot so the live dashboard hot path is unchanged.
            all = aggregateCache.Current.UserAgents;
        }
        // Category values are lowercase strings produced by DashboardUserAgentAggregator.InferUaCategory:
        // "browser", "search", "ai", "tool", "unknown".
        IEnumerable<DashboardUserAgentSummary> filtered = filter switch
        {
            "browser" => all.Where(x => x.Category == "browser"),
            "bot"     => all.Where(x => x.BotRate > 0.5),
            "ai"      => all.Where(x => x.Category is "ai" or "search"),
            "tool"    => all.Where(x => x.Category == "tool"),
            _         => all
        };
        var filteredList = filtered.ToList();
        IEnumerable<DashboardUserAgentSummary> sorted = sort switch
        {
            "family" => dir == "asc" ? filteredList.OrderBy(x => x.Family) : filteredList.OrderByDescending(x => x.Family),
            "category" => dir == "asc" ? filteredList.OrderBy(x => x.Category) : filteredList.OrderByDescending(x => x.Category),
            "botrate" => dir == "asc" ? filteredList.OrderBy(x => x.BotRate) : filteredList.OrderByDescending(x => x.BotRate),
            "confidence" => dir == "asc" ? filteredList.OrderBy(x => x.AvgConfidence) : filteredList.OrderByDescending(x => x.AvgConfidence),
            _ => dir == "asc" ? filteredList.OrderBy(x => x.TotalCount) : filteredList.OrderByDescending(x => x.TotalCount)
        };
        return View(new UserAgentsListModel
        {
            UserAgents = sorted.Skip((page - 1) * pageSize).Take(pageSize).ToList(),
            Filter = filter,
            SortField = sort,
            SortDir = dir,
            Page = page,
            PageSize = pageSize,
            TotalCount = filteredList.Count,
            BasePath = options.Value.BasePath.TrimEnd('/')
        });
    }
}
