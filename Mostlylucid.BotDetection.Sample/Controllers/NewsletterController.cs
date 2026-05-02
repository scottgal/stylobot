using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.TagHelpers;

namespace Mostlylucid.BotDetection.Sample.Controllers;

public class NewsletterController : Controller
{
    [HttpGet]
    public IActionResult Subscribe() => View();

    [HttpPost]
    public IActionResult Subscribe(string email, IFormCollection form)
    {
        // Honeypot: silently accept the submission so bots cannot tell they were caught.
        // The address is never added to the mailing list.
        if (HoneypotValidator.IsTriggered(Request))
            return RedirectToAction(nameof(Thanks));

        // Real high-confidence bot? Same silent-accept treatment.
        if (HttpContext.IsBot() && HttpContext.GetBotProbability() > 0.80)
            return RedirectToAction(nameof(Thanks));

        return RedirectToAction(nameof(Thanks), new { real = true });
    }

    public IActionResult Thanks(bool real = false)
    {
        ViewBag.Real = real;
        return View();
    }
}
