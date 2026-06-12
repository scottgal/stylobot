using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.TrailblazorDemo.Models;
using Mostlylucid.BotDetection.UI.TagHelpers;

namespace Mostlylucid.BotDetection.TrailblazorDemo.Controllers;

public class HomeController : Controller
{
    public IActionResult Index() => View();

    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult Subscribe(string email)
    {
        // Honeypot fields are checked server-side. If a trap was filled,
        // silently drop so the bot operator never learns we caught them.
        if (HoneypotValidator.IsTriggered(Request))
        {
            // Pretend it worked. Don't ban, don't 4xx, don't differentiate.
            return RedirectToAction(nameof(Subscribed));
        }

        // Real handling would go here.
        TempData["Email"] = email;
        return RedirectToAction(nameof(Subscribed));
    }

    public IActionResult Subscribed() => View();

    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public IActionResult Error()
        => View(new ErrorViewModel
        {
            RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier
        });
}