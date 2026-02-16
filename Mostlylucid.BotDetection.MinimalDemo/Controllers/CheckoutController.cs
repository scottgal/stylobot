using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Attributes;
using Mostlylucid.BotDetection.Extensions;

namespace Mostlylucid.BotDetection.MinimalDemo.Controllers;

/// <summary>
///     Demonstrates [BotPolicy] + [BotAction] attributes for config-driven policies.
///     Action policies ("api-throttle", "api-block") are defined in appsettings.json.
/// </summary>
[ApiController]
[Route("[controller]")]
public class CheckoutController : ControllerBase
{
    // GET /checkout — bots get throttled (stealth delay from config)
    [HttpGet]
    [BotPolicy("default", ActionPolicy = "api-throttle")]
    public IActionResult Browse()
    {
        return Ok(new
        {
            message = "Checkout page — bots are throttled (stealth delay)",
            items = new[] { "Widget A x2", "Widget C x1" },
            total = 49.97,
            detection = new
            {
                isBot = HttpContext.IsBot(),
                riskBand = HttpContext.GetRiskBand().ToString()
            }
        });
    }

    // GET /checkout/confirm — bots are hard-blocked (403 from config)
    [HttpGet("confirm")]
    [BotPolicy("default", ActionPolicy = "api-block")]
    public IActionResult Confirm()
    {
        return Ok(new
        {
            message = "Order confirmed!",
            orderId = Guid.NewGuid().ToString("N")[..8]
        });
    }
}
