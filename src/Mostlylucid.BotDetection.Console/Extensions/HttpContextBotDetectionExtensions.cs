using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Console.Extensions;

/// <summary>
///     Extension methods for HttpContext to provide convenient access to bot detection results
/// </summary>
public static class HttpContextBotDetectionExtensions
{
    /// <summary>
    ///     Gets the full bot detection result from the current request
    /// </summary>
    public static BotDetectionResult? GetBotDetectionResult(this HttpContext context)
    {
        if (context.Items.TryGetValue(BotDetectionMiddleware.BotDetectionResultKey, out var resultObj) &&
            resultObj is BotDetectionResult result)
            return result;
        return null;
    }

    /// <summary>
    ///     Indicates whether the current request was detected as a bot
    /// </summary>
    public static bool IsBot(this HttpContext context)
    {
        return context.GetBotDetectionResult()?.IsBot ?? false;
    }

    /// <summary>
    ///     Indicates whether the current request was detected as a human (not a bot)
    /// </summary>
    public static bool IsHuman(this HttpContext context)
    {
        return !context.IsBot();
    }

    /// <summary>
    ///     Indicates whether the bot is considered malicious (high confidence bot, typically >0.7)
    /// </summary>
    public static bool IsMaliciousBot(this HttpContext context)
    {
        var result = context.GetBotDetectionResult();
        return result?.IsBot == true && result.ConfidenceScore >= 0.7;
    }

    /// <summary>
    ///     Gets the bot confidence score (0.0 = definitely human, 1.0 = definitely bot)
    /// </summary>
    public static double BotConfidenceScore(this HttpContext context)
    {
        return context.GetBotDetectionResult()?.ConfidenceScore ?? 0.0;
    }

    /// <summary>
    ///     Gets the bot confidence level as a descriptive enum
    /// </summary>
    public static BotConfidenceLevel BotConfidenceLevel(this HttpContext context)
    {
        var score = context.BotConfidenceScore();
        return score switch
        {
            >= 0.9 => Extensions.BotConfidenceLevel.VeryHigh,
            >= 0.7 => Extensions.BotConfidenceLevel.High,
            >= 0.5 => Extensions.BotConfidenceLevel.Medium,
            >= 0.3 => Extensions.BotConfidenceLevel.Low,
            _ => Extensions.BotConfidenceLevel.VeryLow
        };
    }

    /// <summary>
    ///     Gets the type of bot detected (if any)
    /// </summary>
    public static BotType? BotType(this HttpContext context)
    {
        return context.GetBotDetectionResult()?.BotType;
    }

    /// <summary>
    ///     Gets the name of the bot detected (if identified)
    /// </summary>
    public static string? BotName(this HttpContext context)
    {
        return context.GetBotDetectionResult()?.BotName;
    }

    /// <summary>
    ///     Gets the list of detection reasons explaining why the request was classified as bot/human
    /// </summary>
    public static List<DetectionReason>? BotDetectionReasons(this HttpContext context)
    {
        return context.GetBotDetectionResult()?.Reasons;
    }

    /// <summary>
    ///     Indicates whether the request was blocked by bot detection policies
    /// </summary>
    public static bool WasBlocked(this HttpContext context)
    {
        if (context.Items.TryGetValue("BotDetectionAction", out var actionObj))
            return actionObj?.ToString()?.Contains("Block", StringComparison.OrdinalIgnoreCase) == true;
        return false;
    }

    /// <summary>
    ///     Gets the action that was taken by bot detection (Block, Throttle, Allow, etc.)
    /// </summary>
    public static string? BotDetectionAction(this HttpContext context)
    {
        return context.Items.TryGetValue("BotDetectionAction", out var actionObj)
            ? actionObj?.ToString()
            : null;
    }

    /// <summary>
    ///     Gets the bot detection policy that was applied
    /// </summary>
    public static string? BotDetectionPolicy(this HttpContext context)
    {
        return context.Items.TryGetValue(BotDetectionMiddleware.PolicyNameKey, out var policyObj)
            ? policyObj?.ToString()
            : null;
    }

    /// <summary>
    ///     Gets the primary category of bot detection that triggered
    /// </summary>
    public static string? BotDetectionCategory(this HttpContext context)
    {
        return context.Items.TryGetValue(BotDetectionMiddleware.BotCategoryKey, out var categoryObj)
            ? categoryObj?.ToString()
            : null;
    }

    /// <summary>
    ///     Gets the time taken to perform bot detection
    /// </summary>
    public static TimeSpan? BotDetectionTime(this HttpContext context)
    {
        return context.Items.TryGetValue("BotDetectionTime", out var timeObj) && timeObj is TimeSpan time
            ? time
            : null;
    }
}

/// <summary>
///     Bot confidence level categorization
/// </summary>
public enum BotConfidenceLevel
{
    VeryLow, // 0-30%
    Low, // 30-50%
    Medium, // 50-70%
    High, // 70-90%
    VeryHigh // 90-100%
}