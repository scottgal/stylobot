using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Action filter that blocks bot requests from accessing the endpoint.
///     Use on controllers or actions to protect sensitive endpoints.
/// </summary>
/// <example>
///     [BlockBots] // Blocks all bots
///     public IActionResult SensitiveData() { ... }
///     [BlockBots(AllowVerifiedBots = true)] // Blocks bots except verified ones (Googlebot, etc.)
///     public IActionResult PublicData() { ... }
///     [BlockBots(MinConfidence = 0.9)] // Only blocks high-confidence bot detections
///     public IActionResult ModerateProtection() { ... }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class BlockBotsAttribute : ActionFilterAttribute
{
    /// <summary>
    ///     HTTP status code to return when blocking bots. Default is 403 Forbidden.
    /// </summary>
    public int StatusCode { get; set; } = 403;

    /// <summary>
    ///     Message to return when blocking bots. Default is "Access denied".
    /// </summary>
    public string Message { get; set; } = "Access denied";

    /// <summary>
    ///     If true, verified bots (Googlebot, Bingbot, etc.) are allowed through.
    ///     Default is false.
    /// </summary>
    public bool AllowVerifiedBots { get; set; } = false;

    /// <summary>
    ///     If true, search engine bots are allowed through.
    ///     Default is false.
    /// </summary>
    public bool AllowSearchEngines { get; set; } = false;

    /// <summary>
    ///     Minimum confidence score required to block. Default is 0.0 (block any detected bot).
    ///     Set higher (e.g., 0.8) to only block high-confidence detections.
    /// </summary>
    public double MinConfidence { get; set; } = 0.0;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var result = context.HttpContext.GetBotDetectionResult();

        if (result == null)
        {
            // Detection hasn't run, allow through
            base.OnActionExecuting(context);
            return;
        }

        if (!result.IsBot || result.ConfidenceScore < MinConfidence)
        {
            // Not a bot or below confidence threshold
            base.OnActionExecuting(context);
            return;
        }

        // Check if we should allow this type of bot
        if (AllowVerifiedBots && result.BotType == BotType.VerifiedBot)
        {
            base.OnActionExecuting(context);
            return;
        }

        if (AllowSearchEngines && (result.BotType == BotType.SearchEngine || result.BotType == BotType.VerifiedBot))
        {
            base.OnActionExecuting(context);
            return;
        }

        // Block the bot
        context.Result = new ObjectResult(new
        {
            error = Message,
            isBot = true,
            botType = result.BotType?.ToString(),
            confidence = result.ConfidenceScore
        })
        {
            StatusCode = StatusCode
        };
    }
}

/// <summary>
///     Action filter that explicitly allows bots on the endpoint.
///     Useful when you have global bot blocking but want specific endpoints accessible.
/// </summary>
/// <example>
///     [AllowBots] // All bots allowed
///     public IActionResult RobotsFile() { ... }
///     [AllowBots(OnlyVerified = true)] // Only verified bots allowed
///     public IActionResult Sitemap() { ... }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class AllowBotsAttribute : ActionFilterAttribute
{
    /// <summary>
    ///     If true, only verified bots are allowed, unverified bots are still blocked.
    ///     Default is false (all bots allowed).
    /// </summary>
    public bool OnlyVerified { get; set; } = false;

    /// <summary>
    ///     If true, only search engine bots are allowed.
    ///     Default is false.
    /// </summary>
    public bool OnlySearchEngines { get; set; } = false;

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        // This attribute is primarily for documentation/intent
        // The actual logic would be checked by BlockBotsAttribute
        // But we can add a marker to HttpContext.Items for other filters to check

        context.HttpContext.Items["BotDetection_AllowBots"] = true;
        context.HttpContext.Items["BotDetection_AllowBots_OnlyVerified"] = OnlyVerified;
        context.HttpContext.Items["BotDetection_AllowBots_OnlySearchEngines"] = OnlySearchEngines;

        base.OnActionExecuting(context);
    }
}

/// <summary>
///     Action filter that requires the request to be from a human (not a bot).
///     Stricter than BlockBots - also blocks verified bots.
/// </summary>
/// <example>
///     [RequireHuman]
///     public IActionResult SubmitForm() { ... }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class RequireHumanAttribute : ActionFilterAttribute
{
    /// <summary>
    ///     HTTP status code to return when blocking. Default is 403 Forbidden.
    /// </summary>
    public int StatusCode { get; set; } = 403;

    /// <summary>
    ///     Message to return when blocking.
    /// </summary>
    public string Message { get; set; } = "This action requires human verification";

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var result = context.HttpContext.GetBotDetectionResult();

        if (result == null)
        {
            base.OnActionExecuting(context);
            return;
        }

        if (!result.IsBot)
        {
            base.OnActionExecuting(context);
            return;
        }

        // Block all bots, including verified ones
        context.Result = new ObjectResult(new
        {
            error = Message,
            isBot = true,
            botType = result.BotType?.ToString()
        })
        {
            StatusCode = StatusCode
        };
    }
}