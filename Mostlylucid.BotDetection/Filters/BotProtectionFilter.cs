using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Filters;

/// <summary>
///     Action filter that blocks bot requests from accessing the endpoint.
///     Use on controllers or actions to protect sensitive endpoints.
///     By default blocks ALL bots. Use Allow* properties to whitelist specific bot types.
///     Scrapers and malicious bots are blocked by default but CAN be allowed (e.g., for honeypots).
/// </summary>
/// <example>
///     [BlockBots] // Blocks all bots
///     public IActionResult SensitiveData() { ... }
///
///     [BlockBots(AllowSearchEngines = true)] // Let Googlebot/Bingbot through
///     public IActionResult PublicPage() { ... }
///
///     [BlockBots(AllowSearchEngines = true, AllowSocialMediaBots = true)] // SEO-friendly
///     public IActionResult ShareablePage() { ... }
///
///     [BlockBots(AllowMonitoringBots = true)] // Let UptimeRobot/Pingdom through
///     public IActionResult HealthCheck() { ... }
///
///     [BlockBots(BlockCountries = "CN,RU", BlockVpn = true)] // Geo + network blocking
///     public IActionResult ProtectedApi() { ... }
///
///     [BlockBots(AllowScrapers = true)] // Honeypot endpoint
///     public IActionResult Honeypot() { ... }
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

    // ==========================================
    // Bot type allow flags (all default to false = blocked)
    // ==========================================

    /// <summary>
    ///     If true, DNS-verified bots (Googlebot, Bingbot with verified rDNS) are allowed through.
    ///     Default is false.
    /// </summary>
    public bool AllowVerifiedBots { get; set; }

    /// <summary>
    ///     If true, search engine bots (Googlebot, Bingbot, Yandex, etc.) are allowed through.
    ///     Implies AllowVerifiedBots for search engines.
    ///     Default is false.
    /// </summary>
    public bool AllowSearchEngines { get; set; }

    /// <summary>
    ///     If true, social media bots (Facebook, Twitter/X, LinkedIn crawlers) are allowed through.
    ///     Useful for pages that need Open Graph / link preview support.
    ///     Default is false.
    /// </summary>
    public bool AllowSocialMediaBots { get; set; }

    /// <summary>
    ///     If true, monitoring bots (UptimeRobot, Pingdom, StatusCake, etc.) are allowed through.
    ///     Useful for health check and status endpoints.
    ///     Default is false.
    /// </summary>
    public bool AllowMonitoringBots { get; set; }

    /// <summary>
    ///     If true, AI training bots (GPTBot, ClaudeBot, Google-Extended, etc.) are allowed through.
    ///     Default is false. Most sites want to block these.
    /// </summary>
    public bool AllowAiBots { get; set; }

    /// <summary>
    ///     If true, benign automation (feed readers, link checkers, etc.) is allowed through.
    ///     Default is false.
    /// </summary>
    public bool AllowGoodBots { get; set; }

    /// <summary>
    ///     If true, scrapers (AhrefsBot, SemrushBot, etc.) are allowed through.
    ///     Default is false. Useful for honeypot or research endpoints.
    /// </summary>
    public bool AllowScrapers { get; set; }

    /// <summary>
    ///     If true, known malicious bots are allowed through.
    ///     Default is false. Useful for honeypot endpoints that deliberately attract attackers.
    /// </summary>
    public bool AllowMaliciousBots { get; set; }

    /// <summary>
    ///     Minimum confidence score required to block. Default is 0.0 (block any detected bot).
    ///     Set higher (e.g., 0.8) to only block high-confidence detections.
    /// </summary>
    public double MinConfidence { get; set; }

    // ==========================================
    // Geographic and network blocking
    // ==========================================

    /// <summary>
    ///     Comma-separated ISO 3166-1 alpha-2 country codes to block (e.g., "CN,RU,KP").
    ///     Requires GeoDetection to be registered. Default is null (no country blocking).
    /// </summary>
    public string? BlockCountries { get; set; }

    /// <summary>
    ///     Comma-separated ISO 3166-1 alpha-2 country codes to allow (whitelist mode).
    ///     If set, only traffic from these countries is allowed. Default is null (all countries).
    /// </summary>
    public string? AllowCountries { get; set; }

    /// <summary>
    ///     If true, block requests from VPN connections.
    ///     Requires GeoDetection to be registered. Default is false.
    /// </summary>
    public bool BlockVpn { get; set; }

    /// <summary>
    ///     If true, block requests from proxy servers.
    ///     Default is false.
    /// </summary>
    public bool BlockProxy { get; set; }

    /// <summary>
    ///     If true, block requests from datacenter/hosting IPs (AWS, Azure, GCP, etc.).
    ///     Default is false.
    /// </summary>
    public bool BlockDatacenter { get; set; }

    /// <summary>
    ///     If true, block requests from Tor exit nodes.
    ///     Default is false.
    /// </summary>
    public bool BlockTor { get; set; }

    public override void OnActionExecuting(ActionExecutingContext context)
    {
        var httpContext = context.HttpContext;

        // Check network/geo blocking first (applies to all traffic, not just bots)
        if (BotTypeFilter.IsBlockedByNetwork(httpContext,
                BlockCountries, AllowCountries, BlockVpn, BlockProxy, BlockDatacenter, BlockTor))
        {
            context.Result = new ObjectResult(new
            {
                error = Message,
                blocked = true,
                reason = "network"
            })
            {
                StatusCode = StatusCode
            };
            return;
        }

        var result = httpContext.GetBotDetectionResult();

        if (result == null)
        {
            base.OnActionExecuting(context);
            return;
        }

        if (!result.IsBot || result.ConfidenceScore < MinConfidence)
        {
            base.OnActionExecuting(context);
            return;
        }

        // Check if this bot type is allowed through (shared logic)
        if (BotTypeFilter.IsBotTypeAllowed(result.BotType,
                AllowVerifiedBots, AllowSearchEngines, AllowSocialMediaBots,
                AllowMonitoringBots, AllowAiBots, AllowGoodBots,
                AllowScrapers, AllowMaliciousBots))
        {
            base.OnActionExecuting(context);
            return;
        }

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
