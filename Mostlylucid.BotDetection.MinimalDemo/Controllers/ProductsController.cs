using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Filters;

namespace Mostlylucid.BotDetection.MinimalDemo.Controllers;

/// <summary>
///     Demonstrates [BlockBots] attribute — blocks bots, allows search engines.
/// </summary>
[ApiController]
[Route("[controller]")]
public class ProductsController : ControllerBase
{
    // GET /products — blocks bots, allows search engines
    [HttpGet]
    [BlockBots(AllowSearchEngines = true)]
    public IActionResult List()
    {
        return Ok(new
        {
            products = new[]
            {
                new { id = 1, name = "Widget A", price = 9.99 },
                new { id = 2, name = "Widget B", price = 19.99 },
                new { id = 3, name = "Widget C", price = 29.99 }
            },
            detection = new
            {
                isBot = HttpContext.IsBot(),
                confidence = HttpContext.GetBotConfidence(),
                riskBand = HttpContext.GetRiskBand().ToString()
            }
        });
    }

    // GET /products/5 — no bot protection (open)
    [HttpGet("{id:int}")]
    public IActionResult Get(int id)
    {
        return Ok(new
        {
            id,
            name = $"Widget {(char)('A' + id - 1)}",
            price = id * 9.99,
            detection = new
            {
                isBot = HttpContext.IsBot(),
                botType = HttpContext.GetBotType()?.ToString()
            }
        });
    }
}
