using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

public class SbThreatsListViewComponent(IDashboardEventStore eventStore)
    : ViewComponent
{
    public async Task<IViewComponentResult> InvokeAsync(int count = 20)
    {
        List<ThreatEntry> threats;
        try { threats = await eventStore.GetThreatsAsync(count); }
        catch { threats = []; }

        return View(new ThreatsListModel
        {
            Threats = threats,
            TotalCount = threats.Count,
            ActiveHoneypotSessions = threats.Count(t => t.InHoneypot)
        });
    }
}
