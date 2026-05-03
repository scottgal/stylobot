using Microsoft.AspNetCore.Mvc;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Filters;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Demo.Controllers;

/// <summary>
///     Demo controller showing MVC filter attributes
/// </summary>
[ApiController]
[Route("api/[controller]")]
public class DemoController : ControllerBase
{
    private readonly IBotDetectionService _botService;

    public DemoController(IBotDetectionService botService)
    {
        _botService = botService;
    }

    /// <summary>
    ///     Open endpoint - no bot protection
    /// </summary>
    [HttpGet("open")]
    public IActionResult OpenEndpoint()
    {
        return Ok(new
        {
            endpoint = "open",
            protection = "none",
            isBot = HttpContext.IsBot(),
            botType = HttpContext.GetBotType()?.ToString(),
            message = "This endpoint has no bot protection"
        });
    }

    /// <summary>
    ///     Protected endpoint - blocks all bots except verified ones
    /// </summary>
    [HttpGet("protected")]
    [BlockBots(AllowVerifiedBots = true)]
    public IActionResult ProtectedEndpoint()
    {
        return Ok(new
        {
            endpoint = "protected",
            protection = "BlockBots(AllowVerifiedBots=true)",
            accessGranted = true,
            message = "You passed! Either human or verified bot."
        });
    }

    /// <summary>
    ///     Strict protection - blocks all bots
    /// </summary>
    [HttpGet("strict")]
    [BlockBots]
    public IActionResult StrictEndpoint()
    {
        return Ok(new
        {
            endpoint = "strict",
            protection = "BlockBots",
            accessGranted = true,
            message = "You passed! Must be human."
        });
    }

    /// <summary>
    ///     Human only - absolutely no bots allowed
    /// </summary>
    [HttpGet("human-only")]
    [RequireHuman]
    public IActionResult HumanOnlyEndpoint()
    {
        return Ok(new
        {
            endpoint = "human-only",
            protection = "RequireHuman",
            accessGranted = true,
            message = "Human verified! No bots here."
        });
    }

    /// <summary>
    ///     Search engine friendly - allows Googlebot etc.
    /// </summary>
    [HttpGet("seo-friendly")]
    [BlockBots(AllowSearchEngines = true)]
    public IActionResult SeoFriendlyEndpoint()
    {
        return Ok(new
        {
            endpoint = "seo-friendly",
            protection = "BlockBots(AllowSearchEngines=true)",
            accessGranted = true,
            isSearchEngine = HttpContext.IsSearchEngineBot(),
            botName = HttpContext.GetBotName(),
            message = "Search engines are welcome here!"
        });
    }

    /// <summary>
    ///     High confidence only - only blocks obvious bots
    /// </summary>
    [HttpGet("lenient")]
    [BlockBots(MinConfidence = 0.9)]
    public IActionResult LenientEndpoint()
    {
        return Ok(new
        {
            endpoint = "lenient",
            protection = "BlockBots(MinConfidence=0.9)",
            accessGranted = true,
            confidence = HttpContext.GetBotConfidence(),
            message = "Only blocks bots with >90% confidence"
        });
    }

    /// <summary>
    ///     Get detailed detection analysis
    /// </summary>
    [HttpGet("analyze")]
    public IActionResult AnalyzeRequest()
    {
        var result = HttpContext.GetBotDetectionResult();

        return Ok(new
        {
            // Quick checks via extension methods
            quickChecks = new
            {
                isBot = HttpContext.IsBot(),
                isHuman = HttpContext.IsHuman(),
                isVerifiedBot = HttpContext.IsVerifiedBot(),
                isSearchEngine = HttpContext.IsSearchEngineBot(),
                isMalicious = HttpContext.IsMaliciousBot(),
                shouldAllow = HttpContext.ShouldAllowRequest(),
                shouldBlock = HttpContext.ShouldBlockRequest()
            },
            // Full result
            fullResult = result == null
                ? null
                : new
                {
                    result.IsBot,
                    result.ConfidenceScore,
                    botType = result.BotType?.ToString(),
                    result.BotName,
                    result.ProcessingTimeMs,
                    reasons = result.Reasons.Select(r => new
                    {
                        r.Category,
                        r.Detail,
                        r.ConfidenceImpact
                    })
                },
            // Request info
            requestInfo = new
            {
                userAgent = Request.Headers.UserAgent.ToString(),
                ip = HttpContext.Connection.RemoteIpAddress?.ToString(),
                headers = Request.Headers.Count
            }
        });
    }

    /// <summary>
    ///     Get service statistics
    /// </summary>
    [HttpGet("statistics")]
    public IActionResult GetStatistics()
    {
        var stats = _botService.GetStatistics();

        return Ok(new
        {
            stats.TotalRequests,
            stats.BotsDetected,
            humanVisitors = stats.TotalRequests - stats.BotsDetected,
            botPercentage = stats.TotalRequests > 0
                ? Math.Round((double)stats.BotsDetected / stats.TotalRequests * 100, 2)
                : 0,
            stats.VerifiedBots,
            stats.MaliciousBots,
            averageProcessingMs = Math.Round(stats.AverageProcessingTimeMs, 2),
            stats.BotTypeBreakdown
        });
    }
}