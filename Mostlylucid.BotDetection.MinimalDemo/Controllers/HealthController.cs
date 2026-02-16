using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Attributes;

namespace Mostlylucid.BotDetection.MinimalDemo.Controllers;

/// <summary>
///     Demonstrates [SkipBotDetection] — completely bypasses detection.
///     Use for health checks, metrics, internal endpoints.
/// </summary>
[ApiController]
[Route("[controller]")]
public class HealthController : ControllerBase
{
    // GET /health — no bot detection at all
    [HttpGet]
    [SkipBotDetection]
    public IActionResult Check()
    {
        return Ok(new
        {
            status = "healthy",
            timestamp = DateTimeOffset.UtcNow,
            note = "Bot detection was skipped for this endpoint via [SkipBotDetection]"
        });
    }
}
