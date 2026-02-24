namespace Mostlylucid.BotDetection.Actions;

// ==========================================
// Base Configuration (shared across types)
// ==========================================

/// <summary>
///     Base configuration shared by all configurable components in the bot detection system.
///     Provides common properties for extensibility, categorization, and metadata.
/// </summary>
/// <remarks>
///     <para>
///         This abstract class is inherited by:
///         <list type="bullet">
///             <item>
///                 <see cref="ActionPolicyConfig" /> - Configuration for action policies (Block, Throttle, Challenge,
///                 etc.)
///             </item>
///             <item><see cref="Policies.DetectionPolicyConfig" /> - Configuration for detection policies</item>
///             <item><see cref="Models.DetectorConfig" /> - Configuration for individual detectors</item>
///         </list>
///     </para>
///     <para>
///         All properties support both JSON configuration (appsettings.json) and code configuration.
///         The <see cref="Metadata" /> dictionary allows for custom extensibility without modifying the base classes.
///     </para>
/// </remarks>
/// <example>
///     JSON configuration:
///     <code>
///     {
///       "Enabled": true,
///       "Description": "Custom component for API endpoints",
///       "Priority": 100,
///       "Tags": ["api", "production", "strict"],
///       "Metadata": {
///         "owner": "security-team",
///         "version": "1.2.0"
///       }
///     }
///     </code>
/// </example>
public abstract class BaseComponentConfig
{
    /// <summary>
    ///     Whether this component is enabled.
    ///     Default: true
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Human-readable description of this configuration.
    ///     For documentation and debugging.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Priority/order for this component (higher = earlier).
    ///     Used when multiple components compete.
    ///     Default: 0
    /// </summary>
    public int Priority { get; set; } = 0;

    /// <summary>
    ///     Tags for categorization and filtering.
    ///     Example: ["production", "high-security"]
    /// </summary>
    public List<string>? Tags { get; set; }

    /// <summary>
    ///     Custom metadata for extensibility.
    ///     Can be used by custom implementations.
    /// </summary>
    public Dictionary<string, object>? Metadata { get; set; }
}

// ==========================================
// Action Policy Configuration
// ==========================================

/// <summary>
///     Configuration for a named action policy.
///     Supports full JSON and code configuration for all action policy types.
///     Inherits common extensibility properties from <see cref="BaseComponentConfig" />.
/// </summary>
/// <remarks>
///     <para>
///         Action policies define HOW to respond when a bot is detected (the THEN in the detection flow).
///         They are separate from detection policies (WHAT/WHEN) for maximum composability.
///     </para>
///     <para>
///         Supported action types (via <see cref="Type" /> property):
///         <list type="bullet">
///             <item><b>Block</b> - Return error status code (403, 429, etc.)</item>
///             <item><b>Throttle</b> - Add delay before response, with jitter and risk scaling</item>
///             <item><b>Challenge</b> - Present CAPTCHA, JavaScript challenge, or proof of work</item>
///             <item><b>Redirect</b> - Redirect to honeypot, tarpit, or error page</item>
///             <item><b>LogOnly</b> - Log detection but allow request (shadow mode)</item>
///         </list>
///     </para>
///     <para>
///         Built-in action policies (available without configuration):
///         <list type="bullet">
///             <item>block, block-hard, block-soft, block-debug</item>
///             <item>throttle, throttle-gentle, throttle-moderate, throttle-aggressive, throttle-stealth</item>
///             <item>challenge, challenge-captcha, challenge-js, challenge-pow</item>
///             <item>redirect, redirect-honeypot, redirect-tarpit, redirect-error</item>
///             <item>logonly, shadow, debug, mask-pii, strip-pii (requires ResponsePiiMasking.Enabled = true)</item>
///         </list>
///     </para>
/// </remarks>
/// <example>
///     JSON configuration (appsettings.json):
///     <code>
///     "ActionPolicies": {
///       "myBlock": {
///         "Type": "Block",
///         "StatusCode": 403,
///         "Message": "Access denied",
///         "Headers": { "X-Reason": "bot-detected" },
///         "Description": "Custom block for API endpoints",
///         "Tags": ["api", "strict"]
///       },
///       "stealthThrottle": {
///         "Type": "Throttle",
///         "BaseDelayMs": 500,
///         "MaxDelayMs": 10000,
///         "JitterPercent": 0.5,
///         "ScaleByRisk": true,
///         "IncludeHeaders": false,
///         "Description": "Stealth throttle - bots won't know they're being slowed"
///       }
///     }
///     </code>
///     Code configuration:
///     <code>
///     options.ActionPolicies["myThrottle"] = new ActionPolicyConfig
///     {
///         Type = "Throttle",
///         BaseDelayMs = 1000,
///         JitterPercent = 0.5,
///         ScaleByRisk = true,
///         Description = "Custom throttle for high-value endpoints",
///         Tags = new List&lt;string&gt; { "premium", "high-value" }
///     };
///     </code>
/// </example>
public class ActionPolicyConfig : BaseComponentConfig
{
    /// <summary>
    ///     The type of action policy.
    ///     Required. Values: Block, Throttle, Challenge, Redirect, LogOnly
    /// </summary>
    public string Type { get; set; } = "Block";

    // ==========================================
    // Common Action Properties
    // ==========================================

    /// <summary>
    ///     HTTP status code to return.
    ///     Default: 403 (Block), 429 (Throttle)
    /// </summary>
    public int? StatusCode { get; set; }

    /// <summary>
    ///     Response message/body.
    ///     Default: "Access denied" (Block), "Request throttled" (Throttle)
    /// </summary>
    public string? Message { get; set; }

    /// <summary>
    ///     Content type of the response.
    ///     Default: "application/json"
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>
    ///     Additional headers to add to response.
    ///     Example: { "X-Reason": "bot-detected" }
    /// </summary>
    public Dictionary<string, string>? Headers { get; set; }

    /// <summary>
    ///     Include headers in response (X-Bot-*, X-Throttle-*, etc).
    ///     Default: false (for stealth)
    /// </summary>
    public bool? IncludeHeaders { get; set; }

    // ==========================================
    // Block Policy Options
    // ==========================================

    /// <summary>
    ///     [Block] Include risk score details in response.
    ///     Useful for debugging, disable in production.
    ///     Default: false
    /// </summary>
    public bool? IncludeRiskScore { get; set; }

    // ==========================================
    // Throttle Policy Options
    // ==========================================

    /// <summary>
    ///     [Throttle] Base delay in milliseconds.
    ///     Default: 500
    /// </summary>
    public int? BaseDelayMs { get; set; }

    /// <summary>
    ///     [Throttle] Minimum delay in milliseconds (floor after jitter).
    ///     Default: 100
    /// </summary>
    public int? MinDelayMs { get; set; }

    /// <summary>
    ///     [Throttle] Maximum delay in milliseconds (ceiling).
    ///     Default: 5000
    /// </summary>
    public int? MaxDelayMs { get; set; }

    /// <summary>
    ///     [Throttle] Jitter percentage (0.0-1.0).
    ///     Adds randomness to make throttling less detectable.
    ///     0.25 means +/- 25% variation.
    ///     Default: 0.25
    /// </summary>
    public double? JitterPercent { get; set; }

    /// <summary>
    ///     [Throttle] Scale delay based on risk score.
    ///     Higher risk = longer delay (up to MaxDelayMs).
    ///     Default: true
    /// </summary>
    public bool? ScaleByRisk { get; set; }

    /// <summary>
    ///     [Throttle] Use exponential backoff for repeat requests.
    ///     Each subsequent request increases delay.
    ///     Default: false
    /// </summary>
    public bool? ExponentialBackoff { get; set; }

    /// <summary>
    ///     [Throttle] Backoff multiplier for exponential backoff.
    ///     Default: 2.0
    /// </summary>
    public double? BackoffFactor { get; set; }

    /// <summary>
    ///     [Throttle] Return status code after throttling (vs continue with delay).
    ///     If false, request continues after delay.
    ///     If true, returns StatusCode with Message.
    ///     Default: false
    /// </summary>
    public bool? ReturnStatus { get; set; }

    /// <summary>
    ///     [Throttle] Include Retry-After header.
    ///     Only applies if IncludeHeaders is true.
    ///     Default: true
    /// </summary>
    public bool? IncludeRetryAfter { get; set; }

    // ==========================================
    // Challenge Policy Options
    // ==========================================

    /// <summary>
    ///     [Challenge] Type of challenge: Redirect, Inline, JavaScript, Captcha, ProofOfWork
    ///     Default: Redirect
    /// </summary>
    public string? ChallengeType { get; set; }

    /// <summary>
    ///     [Challenge] URL for challenge page.
    ///     Default: "/challenge"
    /// </summary>
    public string? ChallengeUrl { get; set; }

    /// <summary>
    ///     [Challenge] HTTP status for inline challenges.
    ///     Default: 403
    /// </summary>
    public int? ChallengeStatusCode { get; set; }

    /// <summary>
    ///     [Challenge/Redirect] Query parameter for return URL.
    ///     Default: "returnUrl"
    /// </summary>
    public string? ReturnUrlParam { get; set; }

    /// <summary>
    ///     [Challenge] Track completed challenges with cookies.
    ///     Default: true
    /// </summary>
    public bool? UseTokens { get; set; }

    /// <summary>
    ///     [Challenge] Cookie name for challenge token.
    ///     Default: "bot_challenge_token"
    /// </summary>
    public string? TokenCookieName { get; set; }

    /// <summary>
    ///     [Challenge] Token validity in minutes.
    ///     Default: 30
    /// </summary>
    public int? TokenValidityMinutes { get; set; }

    /// <summary>
    ///     [Challenge] JavaScript file URL for JS challenge type.
    ///     Default: "/scripts/bot-challenge.js"
    /// </summary>
    public string? ChallengeScript { get; set; }

    /// <summary>
    ///     [Challenge] reCAPTCHA/hCaptcha site key.
    /// </summary>
    public string? CaptchaSiteKey { get; set; }

    /// <summary>
    ///     [Challenge] reCAPTCHA/hCaptcha secret key.
    /// </summary>
    public string? CaptchaSecretKey { get; set; }

    /// <summary>
    ///     [Challenge] Title for inline challenge page.
    ///     Default: "Verification Required"
    /// </summary>
    public string? ChallengeTitle { get; set; }

    /// <summary>
    ///     [Challenge] Message for inline challenge page.
    ///     Default: "Please verify that you are human to continue."
    /// </summary>
    public string? ChallengeMessage { get; set; }

    // ==========================================
    // Redirect Policy Options
    // ==========================================

    /// <summary>
    ///     [Redirect] Target URL.
    ///     Supports template placeholders: {risk}, {riskBand}, {policy}, {originalPath}
    ///     Default: "/blocked"
    /// </summary>
    public string? TargetUrl { get; set; }

    /// <summary>
    ///     [Redirect] Use permanent (301) vs temporary (302) redirect.
    ///     Default: false (302 temporary)
    /// </summary>
    public bool? Permanent { get; set; }

    /// <summary>
    ///     [Redirect] Preserve original query string in redirect URL.
    ///     Default: false
    /// </summary>
    public bool? PreserveQueryString { get; set; }

    /// <summary>
    ///     [Redirect] Include return URL parameter.
    ///     Default: false
    /// </summary>
    public bool? IncludeReturnUrl { get; set; }

    /// <summary>
    ///     [Redirect] Add X-Bot-* metadata headers to response.
    ///     Default: false
    /// </summary>
    public bool? AddMetadata { get; set; }

    // ==========================================
    // LogOnly Policy Options
    // ==========================================

    /// <summary>
    ///     [LogOnly] Log level: Trace, Debug, Information, Warning, Error
    ///     Default: Information
    /// </summary>
    public string? LogLevel { get; set; }

    /// <summary>
    ///     [LogOnly] Include full evidence details in logs.
    ///     Default: false
    /// </summary>
    public bool? LogFullEvidence { get; set; }

    /// <summary>
    ///     [LogOnly] Add response headers for debugging.
    ///     Default: false
    /// </summary>
    public bool? AddResponseHeaders { get; set; }

    /// <summary>
    ///     [LogOnly] Include detailed headers (detectors, confidence, bot name).
    ///     Only applies if AddResponseHeaders is true.
    ///     Default: false
    /// </summary>
    public bool? IncludeDetailedHeaders { get; set; }

    /// <summary>
    ///     [LogOnly] Add detection info to HttpContext.Items for downstream access.
    ///     Default: true
    /// </summary>
    public bool? AddToContextItems { get; set; }

    /// <summary>
    ///     [LogOnly] Risk threshold above which "would block" is logged.
    ///     Used for shadow mode comparison.
    ///     Default: 0.85
    /// </summary>
    public double? WouldBlockThreshold { get; set; }

    /// <summary>
    ///     [LogOnly] Custom metric name for telemetry.
    /// </summary>
    public string? MetricName { get; set; }

    // ==========================================
    // Forward Policy Options (LogOnly subtype)
    // ==========================================

    /// <summary>
    ///     [Forward] URL to forward detection data to.
    ///     Supports POST with JSON payload containing full detection evidence.
    ///     Example: "http://honeypot:8080/trap" or "http://analytics:9000/events"
    /// </summary>
    public string? ForwardUrl { get; set; }

    /// <summary>
    ///     [Forward] HTTP method for forwarding.
    ///     Default: POST
    /// </summary>
    public string? ForwardMethod { get; set; }

    /// <summary>
    ///     [Forward] Additional headers to include in forwarded request.
    ///     Example: { "X-Api-Key": "secret", "X-Source": "bot-detection" }
    /// </summary>
    public Dictionary<string, string>? ForwardHeaders { get; set; }

    /// <summary>
    ///     [Forward] Timeout for forward request in milliseconds.
    ///     Default: 5000
    /// </summary>
    public int? ForwardTimeoutMs { get; set; }

    /// <summary>
    ///     [Forward] Fire and forget (don't wait for response).
    ///     Default: true
    /// </summary>
    public bool? ForwardAsync { get; set; }

    /// <summary>
    ///     [Forward] Include full request headers in forwarded payload.
    ///     Default: true
    /// </summary>
    public bool? ForwardIncludeHeaders { get; set; }

    /// <summary>
    ///     [Forward] Include detection reasons/evidence in forwarded payload.
    ///     Default: true
    /// </summary>
    public bool? ForwardIncludeReasons { get; set; }

    // ==========================================
    // LogToFile Policy Options (LogOnly subtype)
    // ==========================================

    /// <summary>
    ///     [LogToFile] Directory for log files.
    ///     Default: "logs/bot-detection"
    /// </summary>
    public string? LogDirectory { get; set; }

    /// <summary>
    ///     [LogToFile] File name pattern with date placeholders.
    ///     {date} = yyyy-MM-dd, {hour} = HH
    ///     Default: "detections-{date}.jsonl"
    /// </summary>
    public string? LogFilePattern { get; set; }

    /// <summary>
    ///     [LogToFile] Only log requests detected as bots.
    ///     Default: true
    /// </summary>
    public bool? LogOnlyBots { get; set; }

    /// <summary>
    ///     [LogToFile] Minimum confidence to log.
    ///     Default: 0.0 (log all)
    /// </summary>
    public double? LogMinConfidence { get; set; }

    /// <summary>
    ///     [LogToFile] Maximum files to retain.
    ///     Default: 30
    /// </summary>
    public int? LogRetainFiles { get; set; }

    // ==========================================
    // Exception/Passthrough Policy Options
    // ==========================================

    /// <summary>
    ///     [Passthrough] User-Agent patterns that bypass blocking (regex).
    ///     Detection still runs for logging, but request is allowed through.
    /// </summary>
    public List<string>? PassthroughUserAgents { get; set; }

    /// <summary>
    ///     [Passthrough] IP addresses/CIDRs that bypass blocking.
    /// </summary>
    public List<string>? PassthroughIps { get; set; }

    /// <summary>
    ///     [Passthrough] Header patterns that indicate passthrough.
    ///     Format: "Header-Name: regex-pattern"
    /// </summary>
    public List<string>? PassthroughHeaders { get; set; }

    /// <summary>
    ///     Converts this configuration to a dictionary for factory consumption.
    /// </summary>
    public IDictionary<string, object> ToDictionary()
    {
        var dict = new Dictionary<string, object>();

        // Type
        dict["Type"] = Type;

        // Base component properties
        dict["Enabled"] = Enabled;
        if (Description != null) dict["Description"] = Description;
        if (Priority != 0) dict["Priority"] = Priority;
        if (Tags != null) dict["Tags"] = Tags;
        if (Metadata != null) dict["Metadata"] = Metadata;

        // Common action properties
        if (StatusCode.HasValue) dict["StatusCode"] = StatusCode.Value;
        if (Message != null) dict["Message"] = Message;
        if (ContentType != null) dict["ContentType"] = ContentType;
        if (Headers != null) dict["Headers"] = Headers;
        if (IncludeHeaders.HasValue) dict["IncludeHeaders"] = IncludeHeaders.Value;

        // Block options
        if (IncludeRiskScore.HasValue) dict["IncludeRiskScore"] = IncludeRiskScore.Value;

        // Throttle options
        if (BaseDelayMs.HasValue) dict["BaseDelayMs"] = BaseDelayMs.Value;
        if (MinDelayMs.HasValue) dict["MinDelayMs"] = MinDelayMs.Value;
        if (MaxDelayMs.HasValue) dict["MaxDelayMs"] = MaxDelayMs.Value;
        if (JitterPercent.HasValue) dict["JitterPercent"] = JitterPercent.Value;
        if (ScaleByRisk.HasValue) dict["ScaleByRisk"] = ScaleByRisk.Value;
        if (ExponentialBackoff.HasValue) dict["ExponentialBackoff"] = ExponentialBackoff.Value;
        if (BackoffFactor.HasValue) dict["BackoffFactor"] = BackoffFactor.Value;
        if (ReturnStatus.HasValue) dict["ReturnStatus"] = ReturnStatus.Value;
        if (IncludeRetryAfter.HasValue) dict["IncludeRetryAfter"] = IncludeRetryAfter.Value;

        // Challenge options
        if (ChallengeType != null) dict["ChallengeType"] = ChallengeType;
        if (ChallengeUrl != null) dict["ChallengeUrl"] = ChallengeUrl;
        if (ChallengeStatusCode.HasValue) dict["ChallengeStatusCode"] = ChallengeStatusCode.Value;
        if (ReturnUrlParam != null) dict["ReturnUrlParam"] = ReturnUrlParam;
        if (UseTokens.HasValue) dict["UseTokens"] = UseTokens.Value;
        if (TokenCookieName != null) dict["TokenCookieName"] = TokenCookieName;
        if (TokenValidityMinutes.HasValue) dict["TokenValidityMinutes"] = TokenValidityMinutes.Value;
        if (ChallengeScript != null) dict["ChallengeScript"] = ChallengeScript;
        if (CaptchaSiteKey != null) dict["CaptchaSiteKey"] = CaptchaSiteKey;
        if (CaptchaSecretKey != null) dict["CaptchaSecretKey"] = CaptchaSecretKey;
        if (ChallengeTitle != null) dict["ChallengeTitle"] = ChallengeTitle;
        if (ChallengeMessage != null) dict["ChallengeMessage"] = ChallengeMessage;

        // Redirect options
        if (TargetUrl != null) dict["TargetUrl"] = TargetUrl;
        if (Permanent.HasValue) dict["Permanent"] = Permanent.Value;
        if (PreserveQueryString.HasValue) dict["PreserveQueryString"] = PreserveQueryString.Value;
        if (IncludeReturnUrl.HasValue) dict["IncludeReturnUrl"] = IncludeReturnUrl.Value;
        if (AddMetadata.HasValue) dict["AddMetadata"] = AddMetadata.Value;

        // LogOnly options
        if (LogLevel != null) dict["LogLevel"] = LogLevel;
        if (LogFullEvidence.HasValue) dict["LogFullEvidence"] = LogFullEvidence.Value;
        if (AddResponseHeaders.HasValue) dict["AddResponseHeaders"] = AddResponseHeaders.Value;
        if (IncludeDetailedHeaders.HasValue) dict["IncludeDetailedHeaders"] = IncludeDetailedHeaders.Value;
        if (AddToContextItems.HasValue) dict["AddToContextItems"] = AddToContextItems.Value;
        if (WouldBlockThreshold.HasValue) dict["WouldBlockThreshold"] = WouldBlockThreshold.Value;
        if (MetricName != null) dict["MetricName"] = MetricName;

        // Forward options
        if (ForwardUrl != null) dict["ForwardUrl"] = ForwardUrl;
        if (ForwardMethod != null) dict["ForwardMethod"] = ForwardMethod;
        if (ForwardHeaders != null) dict["ForwardHeaders"] = ForwardHeaders;
        if (ForwardTimeoutMs.HasValue) dict["ForwardTimeoutMs"] = ForwardTimeoutMs.Value;
        if (ForwardAsync.HasValue) dict["ForwardAsync"] = ForwardAsync.Value;
        if (ForwardIncludeHeaders.HasValue) dict["ForwardIncludeHeaders"] = ForwardIncludeHeaders.Value;
        if (ForwardIncludeReasons.HasValue) dict["ForwardIncludeReasons"] = ForwardIncludeReasons.Value;

        // LogToFile options
        if (LogDirectory != null) dict["LogDirectory"] = LogDirectory;
        if (LogFilePattern != null) dict["LogFilePattern"] = LogFilePattern;
        if (LogOnlyBots.HasValue) dict["LogOnlyBots"] = LogOnlyBots.Value;
        if (LogMinConfidence.HasValue) dict["LogMinConfidence"] = LogMinConfidence.Value;
        if (LogRetainFiles.HasValue) dict["LogRetainFiles"] = LogRetainFiles.Value;

        // Passthrough options
        if (PassthroughUserAgents != null) dict["PassthroughUserAgents"] = PassthroughUserAgents;
        if (PassthroughIps != null) dict["PassthroughIps"] = PassthroughIps;
        if (PassthroughHeaders != null) dict["PassthroughHeaders"] = PassthroughHeaders;

        return dict;
    }
}
