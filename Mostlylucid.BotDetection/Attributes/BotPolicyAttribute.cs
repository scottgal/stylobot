using Microsoft.AspNetCore.Mvc.Filters;

namespace Mostlylucid.BotDetection.Attributes;

/// <summary>
///     Specifies the bot detection policy to use for a controller or action.
///     Can be applied at controller level (affects all actions) or action level (overrides controller).
/// </summary>
/// <example>
///     // Apply strict policy to entire controller
///     [BotPolicy("strict")]
///     public class PaymentController : Controller { }
///     // Apply different policies per action
///     public class AccountController : Controller
///     {
///     [BotPolicy("strict")]
///     public IActionResult Login() { }
///     [BotPolicy("relaxed")]
///     public IActionResult PublicProfile() { }
///     }
///     // Override action policy for specific endpoint
///     [BotPolicy("strict", ActionPolicy = "throttle-stealth")]
///     public IActionResult ProtectedApi() { }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class BotPolicyAttribute : Attribute, IFilterMetadata
{
    /// <summary>
    ///     Creates a new BotPolicyAttribute with the specified policy name.
    /// </summary>
    /// <param name="policyName">Name of the policy to use (e.g., "strict", "relaxed", "default")</param>
    public BotPolicyAttribute(string policyName)
    {
        PolicyName = policyName ?? throw new ArgumentNullException(nameof(policyName));
    }

    /// <summary>
    ///     The name of the bot detection policy to apply.
    /// </summary>
    public string PolicyName { get; }

    /// <summary>
    ///     If true, skip bot detection entirely for this endpoint.
    ///     Useful for health checks, metrics endpoints, etc.
    /// </summary>
    public bool Skip { get; set; }

    /// <summary>
    ///     Override the block action for this endpoint.
    ///     If null, uses the policy's default action.
    /// </summary>
    public BotBlockAction BlockAction { get; set; } = BotBlockAction.Default;

    /// <summary>
    ///     Custom status code to return when blocking (if BlockAction is StatusCode).
    /// </summary>
    public int BlockStatusCode { get; set; } = 403;

    /// <summary>
    ///     Custom redirect URL when blocking (if BlockAction is Redirect).
    /// </summary>
    public string? BlockRedirectUrl { get; set; }

    /// <summary>
    ///     Override the immediate block threshold for this endpoint.
    ///     If set, overrides the policy's threshold.
    ///     Range: 0.0-1.0
    /// </summary>
    public double BlockThreshold { get; set; } = -1; // -1 means use policy default

    /// <summary>
    ///     Name of the action policy to use for this endpoint.
    ///     Overrides the detection policy's default action policy.
    ///     Reference any named action policy (built-in or custom).
    /// </summary>
    /// <example>
    ///     [BotPolicy("strict", ActionPolicy = "block-hard")]
    ///     [BotPolicy("relaxed", ActionPolicy = "throttle-stealth")]
    ///     [BotPolicy("default", ActionPolicy = "shadow")] // Log-only mode
    /// </example>
    public string? ActionPolicy { get; set; }
}

/// <summary>
///     Attribute to run a single detector inline for simple ad-hoc use cases.
///     Allows quick configuration without defining a full policy.
/// </summary>
/// <example>
///     // Simple detector with defaults
///     [BotDetector("UserAgent")]
///     public IActionResult QuickCheck() { }
///     // Detector with custom weight and threshold
///     [BotDetector("Behavioral", Weight = 2.0, BlockThreshold = 0.8)]
///     public IActionResult RateLimitedEndpoint() { }
///     // Multiple detectors inline
///     [BotDetector("UserAgent,Header,Ip", BlockAction = BotBlockAction.Throttle)]
///     public IActionResult MultiDetector() { }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class BotDetectorAttribute : Attribute, IFilterMetadata
{
    /// <summary>
    ///     Creates a new BotDetectorAttribute with the specified detector(s).
    /// </summary>
    /// <param name="detectors">
    ///     Comma-separated list of detector names to run.
    ///     Available: UserAgent, Header, Ip, Behavioral, Inconsistency, ClientSide, Onnx, Llm
    /// </param>
    public BotDetectorAttribute(string detectors)
    {
        Detectors = detectors ?? throw new ArgumentNullException(nameof(detectors));
    }

    /// <summary>
    ///     Comma-separated list of detector names to run.
    /// </summary>
    public string Detectors { get; }

    /// <summary>
    ///     Weight multiplier for all specified detectors.
    ///     Higher = more influence on final score.
    ///     Default: 1.0
    /// </summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>
    ///     Risk threshold above which to block.
    ///     Default: 0.85
    /// </summary>
    public double BlockThreshold { get; set; } = 0.85;

    /// <summary>
    ///     Risk threshold below which to allow immediately (early exit).
    ///     Default: 0.3
    /// </summary>
    public double AllowThreshold { get; set; } = 0.3;

    /// <summary>
    ///     Action to take when block threshold is exceeded.
    ///     Default: StatusCode (403)
    /// </summary>
    public BotBlockAction BlockAction { get; set; } = BotBlockAction.StatusCode;

    /// <summary>
    ///     HTTP status code for block action.
    ///     Default: 403
    /// </summary>
    public int BlockStatusCode { get; set; } = 403;

    /// <summary>
    ///     If true, skip bot detection entirely.
    /// </summary>
    public bool Skip { get; set; }

    /// <summary>
    ///     Timeout in milliseconds for detection.
    ///     Default: 1000 (1 second)
    /// </summary>
    public int TimeoutMs { get; set; } = 1000;

    /// <summary>
    ///     Name of the action policy to use when block threshold is exceeded.
    ///     Overrides the BlockAction enum if specified.
    ///     Reference any named action policy (built-in or custom).
    /// </summary>
    /// <example>
    ///     [BotDetector("UserAgent,Header", ActionPolicy = "throttle-stealth")]
    ///     [BotDetector("Behavioral", ActionPolicy = "challenge-captcha")]
    /// </example>
    public string? ActionPolicy { get; set; }

    /// <summary>
    ///     Gets the list of detector names.
    /// </summary>
    public IReadOnlyList<string> GetDetectorList()
    {
        return Detectors.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

/// <summary>
///     Marks an endpoint to skip bot detection entirely.
/// </summary>
/// <example>
///     [SkipBotDetection]
///     public IActionResult HealthCheck() { }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class SkipBotDetectionAttribute : Attribute, IFilterMetadata
{
}

/// <summary>
///     Specifies a named action policy to use for bot handling on this endpoint.
///     Use alongside [BotPolicy] to override the default action policy.
/// </summary>
/// <example>
///     // Use with detection policy
///     [BotPolicy("strict")]
///     [BotAction("block-hard")]
///     public IActionResult Login() { }
///     // Use alone (applies to all detection policies on this endpoint)
///     [BotAction("throttle-stealth")]
///     public IActionResult Api() { }
///     // Multiple actions based on risk level (using custom transitions)
///     [BotAction("challenge", FallbackAction = "block")]
///     public IActionResult Checkout() { }
/// </example>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public class BotActionAttribute : Attribute, IFilterMetadata
{
    /// <summary>
    ///     Creates a new BotActionAttribute with the specified action policy name.
    /// </summary>
    /// <param name="policyName">
    ///     Name of the action policy to use.
    ///     Built-in: block, block-hard, block-soft, throttle, throttle-stealth,
    ///     challenge, challenge-captcha, redirect, logonly, shadow, debug
    /// </param>
    public BotActionAttribute(string policyName)
    {
        PolicyName = policyName ?? throw new ArgumentNullException(nameof(policyName));
    }

    /// <summary>
    ///     The name of the action policy to apply.
    /// </summary>
    public string PolicyName { get; }

    /// <summary>
    ///     Fallback action policy name if the primary action fails.
    ///     For example, if challenge service is unavailable, fall back to block.
    /// </summary>
    public string? FallbackAction { get; set; }

    /// <summary>
    ///     Minimum risk threshold to trigger this action.
    ///     If set, only applies when risk score >= this value.
    ///     Range: 0.0-1.0. Default: 0 (always applies when blocking)
    /// </summary>
    public double MinRiskThreshold { get; set; } = 0;
}

/// <summary>
///     Action to take when a bot is blocked.
/// </summary>
public enum BotBlockAction
{
    /// <summary>Use the policy's default action</summary>
    Default,

    /// <summary>Return a status code (default 403)</summary>
    StatusCode,

    /// <summary>Redirect to a URL</summary>
    Redirect,

    /// <summary>Show a challenge page (CAPTCHA)</summary>
    Challenge,

    /// <summary>Throttle/rate limit the request</summary>
    Throttle,

    /// <summary>Log only, don't actually block (shadow mode)</summary>
    LogOnly
}