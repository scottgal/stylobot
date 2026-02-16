using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Filters;

namespace Mostlylucid.BotDetection.MinimalDemo.Controllers;

/// <summary>
///     Demonstrates [RequireHuman] on an entire controller.
///     ALL actions inherit the restriction.
/// </summary>
[ApiController]
[Route("[controller]")]
[RequireHuman]
public class AdminController : ControllerBase
{
    // GET /admin — humans only
    [HttpGet]
    public IActionResult Dashboard()
    {
        return Ok(new
        {
            message = "Admin dashboard — humans only",
            confidence = HttpContext.GetBotConfidence(),
            riskBand = HttpContext.GetRiskBand().ToString()
        });
    }

    // GET /admin/settings — humans only (inherited)
    [HttpGet("settings")]
    public IActionResult Settings()
    {
        return Ok(new
        {
            message = "Admin settings",
            settings = new { botThreshold = 0.7, defaultPolicy = "throttle-stealth" }
        });
    }
}
