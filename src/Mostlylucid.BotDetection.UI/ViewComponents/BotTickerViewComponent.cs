using Microsoft.AspNetCore.Mvc;

namespace Mostlylucid.BotDetection.UI.ViewComponents;

/// <summary>
///     View component for displaying real-time bot detection ticker.
///     Connects to SignalR hub for live updates.
/// </summary>
public class BotTickerViewComponent : ViewComponent
{
    public IViewComponentResult Invoke()
    {
        return View();
    }
}
