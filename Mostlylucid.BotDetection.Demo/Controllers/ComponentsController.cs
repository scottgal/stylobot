using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.TagHelpers;

namespace Mostlylucid.BotDetection.Demo.Controllers;

/// <summary>
///     MVC controller demonstrating all sb-* TagHelpers in a standard
///     Controller → View workflow with a shared Layout.
/// </summary>
public class ComponentsController : Controller
{
    /// <summary>
    ///     Main page — all data-display and gating TagHelpers rendered live.
    /// </summary>
    public IActionResult Index()
    {
        ViewData["Title"] = "sb-* Component TagHelpers";
        ViewData["Subtitle"] = "ASP.NET Core MVC demo — content gating, data display, and form protection";
        return View();
    }

    /// <summary>
    ///     Dedicated content-gating demo with explanations.
    /// </summary>
    public IActionResult Gating()
    {
        ViewData["Title"] = "Content Gating";
        ViewData["Subtitle"] = "Show or hide content based on detection results";
        return View();
    }

    /// <summary>
    ///     Honeypot form protection demo with POST handler.
    /// </summary>
    [HttpGet]
    public IActionResult Honeypot()
    {
        ViewData["Title"] = "Honeypot Form Protection";
        ViewData["Subtitle"] = "Hidden trap fields that catch automated form submissions";
        return View(new HoneypotDemoModel());
    }

    [HttpPost]
    public IActionResult Honeypot(HoneypotDemoModel model)
    {
        ViewData["Title"] = "Honeypot Form Protection";
        ViewData["Subtitle"] = "Hidden trap fields that catch automated form submissions";

        model.IsSubmitted = true;
        model.IsTriggered = HoneypotValidator.IsTriggered(Request);
        return View(model);
    }
}

public class HoneypotDemoModel
{
    public string? Name { get; set; }
    public string? Email { get; set; }
    public string? Message { get; set; }
    public bool IsSubmitted { get; set; }
    public bool IsTriggered { get; set; }
}
