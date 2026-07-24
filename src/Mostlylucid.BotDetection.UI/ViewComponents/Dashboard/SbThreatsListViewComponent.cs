using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbThreatsListViewComponent(IDashboardEventStore eventStore, StyloBotDashboardOptions options)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(int page = 1, int pageSize = 20)
    {
        // If the page composer stashed a DashboardPageResult for this request, read the
        // Threats slice from it directly (zero store calls). Otherwise fall through to the
        // existing self-fetch so VCs rendered on non-composer pages still work.
        var pageResult = HttpContext?.Items["sb.dashboard.pageresult"] as DashboardPageResult;
        IReadOnlyList<ThreatEntry> allThreats = pageResult?.ThreatsRaw is { } composedThreats
            ? composedThreats
            : await eventStore.GetThreatsAsync(pageSize * 10);

        var totalCount = allThreats.Count;
        var threats = allThreats
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return View(new ThreatsListModel
        {
            Threats = threats,
            TotalCount = totalCount,
            ActiveHoneypotSessions = allThreats.Count(t => t.InHoneypot),
            Page = page,
            PageSize = pageSize,
            BasePath = options.BasePath.TrimEnd('/')
        });
    }
}
