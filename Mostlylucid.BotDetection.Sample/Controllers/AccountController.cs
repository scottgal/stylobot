using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.UI.TagHelpers;

namespace Mostlylucid.BotDetection.Sample.Controllers;

public class AccountController : Controller
{
    [HttpGet]
    public IActionResult Login() => View();

    [HttpPost]
    public IActionResult Login(string email, string password, IFormCollection form)
    {
        // Honeypot guard: bots often fill all visible fields including hidden traps
        if (HoneypotValidator.IsTriggered(Request))
            return View("LoginDenied");

        // In a real app: validate credentials here.
        // Redirect to home to complete the tutorial loop.
        return RedirectToAction("Index", "Home");
    }
}
