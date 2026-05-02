using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.TagHelpers;

namespace Mostlylucid.BotDetection.Sample.Controllers;

public class CartController : Controller
{
    [HttpGet]
    public IActionResult Checkout() => View();

    [HttpPost]
    public IActionResult Checkout(IFormCollection form)
    {
        // StyloBot honeypot: if hidden trap fields were filled, the submitter is a bot.
        // We redirect silently rather than returning an error so bots cannot learn the check exists.
        if (HoneypotValidator.IsTriggered(Request))
            return RedirectToAction(nameof(Checkout));

        return RedirectToAction(nameof(Confirmed));
    }

    public IActionResult Confirmed() => View();
}
