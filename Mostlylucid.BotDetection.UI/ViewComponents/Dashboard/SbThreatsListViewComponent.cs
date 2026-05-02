using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbThreatsListViewComponent(IDashboardEventStore eventStore)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(int page = 1, int pageSize = 20)
    {
        List<ThreatEntry> allThreats;
        try { allThreats = await eventStore.GetThreatsAsync(pageSize * 10); }
        catch { allThreats = []; }

        var totalCount = allThreats.Count;
        var threats = allThreats
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return View(new ThreatsListModel
        {
            Threats = threats,
            TotalCount = totalCount,
            ActiveHoneypotSessions = allThreats.Count(t => t.InHoneypot)
        });
    }
}
