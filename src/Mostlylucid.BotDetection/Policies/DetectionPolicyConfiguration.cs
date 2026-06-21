using System.Collections.Immutable;
using Mostlylucid.BotDetection.Actions;

namespace Mostlylucid.BotDetection.Policies;

// ==========================================
// Detection Policy Configuration
// ==========================================

/// <summary>
///     Configuration for a named detection policy.
///     Policies can be assigned to paths for different detection behaviour.
///     Inherits common extensibility properties from <see cref="BaseComponentConfig" />.
/// </summary>
/// <remarks>
///     <para>
///         Detection policies define WHAT to detect and HOW to detect it, including:
///         <list type="bullet">
///             <item>Which detectors to run (FastPath, SlowPath, AiPath)</item>
///             <item>Risk thresholds for early exit, blocking, and AI escalation</item>
///             <item>Detector weight overrides for customized scoring</item>
///             <item>Transitions based on signals and risk scores</item>
///             <item>Link to action policy (THEN) for response handling</item>
///         </list>
///     </para>
///     <para>
///         Built-in detection policies: default, strict, relaxed, allowVerifiedBots
///     </para>
///     <para>
///         Built-in action policies: block, throttle, challenge, redirect, logonly (and variants)
///     </para>
/// </remarks>
/// <example>
///     JSON configuration (appsettings.json):
///     <code>
///     "Policies": {
///       "strict": {
///         "Description": "High-security detection with response analysis",
///         "FastPath": ["UserAgent", "Header", "Ip"],
///         "SlowPath": ["Behavioral", "Inconsistency", "ClientSide"],
///         "AiPath": ["Onnx", "Llm"],
///         "ResponsePath": ["ResponseCoordinator", "HoneypotAnalyzer"],
///         "ForceSlowPath": true,
///         "EscalateToAi": true,
///         "AiEscalationThreshold": 0.4,
///         "ImmediateBlockThreshold": 0.9,
///         "ActionPolicyName": "block-hard",
///         "Weights": {
///           "Behavioral": 2.0,
///           "Inconsistency": 2.0
///         },
///         "Transitions": [
///           { "WhenRiskExceeds": 0.9, "ActionPolicyName": "block-hard" },
///           { "WhenSignal": "VerifiedGoodBot", "Action": "Allow" }
///         ],
///         "Tags": ["high-security", "payments", "response-tracking"]
///       },
///       "relaxed": {
///         "Description": "Minimal detection for public content",
///         "FastPath": ["UserAgent"],
///         "SlowPath": [],
///         "AiPath": [],
///         "EarlyExitThreshold": 0.5,
///         "ImmediateBlockThreshold": 0.99,
///         "ActionPolicyName": "logonly",
///         "Tags": ["public", "static"]
///       }
///     }
///     </code>
///     Code configuration:
///     <code>
///     services.AddBotDetection(options =>
///     {
///         options.Policies["custom"] = new DetectionPolicyConfig
///         {
///             Description = "Custom policy for API endpoints",
///             FastPath = new List&lt;string&gt; { "UserAgent", "Header", "Ip" },
///             SlowPath = new List&lt;string&gt; { "Behavioral" },
///             ForceSlowPath = true,
///             ActionPolicyName = "throttle-stealth",
///             Tags = new List&lt;string&gt; { "api", "rate-limited" }
///         };
///     });
///     </code>
/// </example>
public class DetectionPolicyConfig : BaseComponentConfig
{
    // ==========================================
    // Detector Configuration
    // ==========================================

    /// <summary>
    ///     Fast path detector names to run (synchronous, in-request).
    ///     These run in Wave 0 and are expected to complete quickly (&lt;100ms).
    ///     Default: [] (empty = run ALL registered detectors when combined with SlowPath/AiPath)
    /// </summary>
    public List<string> FastPath { get; set; } = [];

    /// <summary>
    ///     Slow path detector names to run (async when triggered).
    ///     These run in subsequent waves and may include expensive analysis.
    ///     Default: [] (empty = run ALL registered detectors when combined with FastPath/AiPath)
    /// </summary>
    public List<string> SlowPath { get; set; } = [];

    /// <summary>
    ///     AI detector names to run (when escalated).
    ///     Only run when escalated by other detectors or risk threshold.
    ///     Default: [] (empty = run ALL registered AI detectors when EscalateToAi=true)
    /// </summary>
    public List<string> AiPath { get; set; } = [];

    /// <summary>
    ///     Response path detector names to run AFTER response is generated (post-request analysis).
    ///     These detectors analyze response patterns, honeypot interactions, error harvesting, etc.
    ///     Results feed back into future request detections via ResponseBehaviorContributor.
    ///     Examples: "ResponseCoordinator", "HoneypotAnalyzer", "ErrorPatternDetector"
    ///     Default: [] (empty = no response-path analysis)
    /// </summary>
    /// <remarks>
    ///     Response path detectors run asynchronously AFTER the response has been sent to the client.
    ///     They do not block the response. They are used for:
    ///     - Tracking honeypot path access
    ///     - Analyzing 404 scanning patterns
    ///     - Detecting error message harvesting
    ///     - Building historical behavior profiles
    ///     Results are available to future requests via ResponseBehaviorContributor.
    /// </remarks>
    public List<string> ResponsePath { get; set; } = [];

    // ==========================================
    // Detection Flow Control
    // ==========================================

    /// <summary>
    ///     Whether to use the fast path at all.
    ///     Set to false for always-deep analysis.
    ///     Default: true
    /// </summary>
    public bool UseFastPath { get; set; } = true;

    /// <summary>
    ///     Force slow path to run even if fast path is conclusive.
    ///     Useful for high-security endpoints.
    ///     Default: false
    /// </summary>
    public bool ForceSlowPath { get; set; }

    /// <summary>
    ///     Enable AI escalation when risk exceeds threshold.
    ///     Default: false
    /// </summary>
    public bool EscalateToAi { get; set; }

    // ==========================================
    // Thresholds
    // ==========================================

    /// <summary>
    ///     Risk threshold above which to escalate to AI detectors.
    ///     Default: 0.6 (60% confidence of being a bot)
    /// </summary>
    public double AiEscalationThreshold { get; set; } = 0.6;

    /// <summary>
    ///     Risk threshold below which to allow early exit from fast path.
    ///     Default: 0.3 (30% confidence = likely human)
    /// </summary>
    public double EarlyExitThreshold { get; set; } = 0.3;

    /// <summary>
    ///     Risk threshold above which to block immediately without further analysis.
    ///     Default: 0.95 (95% confidence = definitely a bot)
    /// </summary>
    public double ImmediateBlockThreshold { get; set; } = 0.95;

    /// <summary>
    ///     Minimum confidence required before any blocking decision takes effect.
    ///     Even if bot probability exceeds <see cref="ImmediateBlockThreshold" />,
    ///     blocking only occurs when confidence meets this gate.
    ///     Default: 0.0 (no confidence gate - backwards compatible).
    /// </summary>
    /// <example>
    ///     "strict": { "ImmediateBlockThreshold": 0.7, "MinConfidence": 0.9 }
    /// </example>
    public double MinConfidence { get; set; }

    // ==========================================
    // Detector Weights
    // ==========================================

    /// <summary>
    ///     Detector weight overrides for this policy.
    ///     Key = detector name, Value = weight multiplier.
    ///     Higher weight = more influence on final decision.
    ///     Example: { "Behavioral": 2.0, "Inconsistency": 1.5 }
    /// </summary>
    public Dictionary<string, double> Weights { get; set; } = new();

    // ==========================================
    // Action Policy Linkage
    // ==========================================

    /// <summary>
    ///     Name of the action policy to use when this detection policy triggers blocking.
    ///     If null, uses DefaultActionPolicyName from global options.
    ///     Built-in policies: block, throttle, challenge, redirect, logonly
    /// </summary>
    /// <example>
    ///     "strict": { "ActionPolicyName": "block-hard" },
    ///     "relaxed": { "ActionPolicyName": "throttle-gentle" }
    /// </example>
    public string? ActionPolicyName { get; set; }

    /// <summary>
    ///     Whether API keys can override the action policy for this detection policy.
    ///     When false, the policy's ActionPolicyName and transitions take precedence
    ///     over any API key ActionPolicyName override.
    ///     Default: true (API keys can override).
    /// </summary>
    public bool ActionPolicyOverridable { get; set; } = true;

    /// <summary>
    ///     Fallback action policy name when primary action fails.
    ///     For example, if challenge service is unavailable, fall back to block.
    /// </summary>
    public string? FallbackActionPolicyName { get; set; }

    // ==========================================
    // Transitions
    // ==========================================

    /// <summary>
    ///     Policy transitions based on conditions.
    ///     Transitions allow dynamic policy selection based on detection results.
    /// </summary>
    public List<TransitionConfig> Transitions { get; set; } = [];

    // ==========================================
    // Timeout
    // ==========================================

    /// <summary>
    ///     Timeout in milliseconds for this policy's detection pipeline.
    ///     Default: 5000 (5 seconds)
    /// </summary>
    public int TimeoutMs { get; set; } = 5000;

    /// <summary>
    ///     When true, all detectors run in Wave 0 regardless of their TriggerConditions.
    ///     Primary use case: learning/slow path where full characterization is required.
    ///     Also useful for demo/testing to ensure the full detection pipeline runs.
    ///     Default: false (respect trigger conditions for optimal performance).
    /// </summary>
    public bool BypassTriggerConditions { get; set; }

    // ==========================================
    // Failure & Load-Shed Behaviour
    // ==========================================

    /// <summary>
    ///     What to do when bot detection itself fails (orchestrator exception, store
    ///     unavailable, sidecar unreachable). String form of <see cref="FailureMode"/>:
    ///     "FailOpen" (default), "FailClosed", or "LogOnly". Unknown values fall back
    ///     to <see cref="FailureMode.FailOpen"/>.
    /// </summary>
    /// <example>
    ///     "admin": { "OnFailure": "FailClosed" }
    /// </example>
    public string? OnFailure { get; set; }

    /// <summary>
    ///     Per-policy load-shed configuration. When the pipeline is under High or
    ///     Critical load, the configured fraction of requests skips detection.
    ///     Defaults to zero (no shedding). Opt-in.
    /// </summary>
    /// <example>
    ///     "admin": { "LoadShed": { "DropFractionAtHigh": 0.0, "DropFractionAtCritical": 0.1 } }
    /// </example>
    public LoadShedDef? LoadShed { get; set; }

    /// <summary>
    ///     Per-policy signature verdict cache thresholds. Controls SignatureVerdictGate
    ///     Skip/Bias/Miss behaviour for this policy. Defaults to enabled with general-
    ///     purpose thresholds; high-security endpoints should set Enabled=false.
    /// </summary>
    /// <example>
    ///     "admin": { "SignatureCache": { "Enabled": false } }
    /// </example>
    public SignatureCacheDef? SignatureCache { get; set; }

    /// <summary>
    ///     Converts this configuration to a DetectionPolicy record.
    /// </summary>
    public DetectionPolicy ToPolicy(string name)
    {
        var onFailure = Enum.TryParse<FailureMode>(OnFailure, ignoreCase: true, out var mode)
            ? mode
            : FailureMode.FailOpen;

        var loadShed = LoadShed is { } ls
            ? new LoadShedOptions
            {
                DropFractionAtHigh = ls.DropFractionAtHigh,
                DropFractionAtCritical = ls.DropFractionAtCritical,
            }
            : new LoadShedOptions();

        var signatureCache = SignatureCache is { } sc
            ? new SignatureCacheOptions
            {
                Enabled = sc.Enabled,
                SkipMinConfidence = sc.SkipMinConfidence,
                SkipMaxAgeSeconds = sc.SkipMaxAgeSeconds,
                BiasMinConfidence = sc.BiasMinConfidence,
                BiasMaxAgeSeconds = sc.BiasMaxAgeSeconds,
                SkipSamplingRate = sc.SkipSamplingRate,
                Watchdog = sc.Watchdog is { } wd
                    ? new VarianceWatchdogOptions
                    {
                        Enabled = wd.Enabled,
                        IpRotationWindowSeconds = wd.IpRotationWindowSeconds,
                        CheckPathCentroid = wd.CheckPathCentroid,
                        RateSpikeMultiplier = wd.RateSpikeMultiplier,
                    }
                    : new VarianceWatchdogOptions(),
            }
            : new SignatureCacheOptions();

        return new DetectionPolicy
        {
            Name = name,
            Description = Description,
            FastPathDetectors = [..FastPath],
            SlowPathDetectors = [..SlowPath],
            AiPathDetectors = [..AiPath],
            UseFastPath = UseFastPath,
            ForceSlowPath = ForceSlowPath,
            EscalateToAi = EscalateToAi,
            AiEscalationThreshold = AiEscalationThreshold,
            EarlyExitThreshold = EarlyExitThreshold,
            ImmediateBlockThreshold = ImmediateBlockThreshold,
            MinConfidence = MinConfidence,
            WeightOverrides = Weights.ToImmutableDictionary(),
            Transitions = Transitions.Select(t => t.ToTransition()).ToImmutableList(),
            Timeout = TimeSpan.FromMilliseconds(TimeoutMs),
            Enabled = Enabled,
            BypassTriggerConditions = BypassTriggerConditions,
            ActionPolicyOverridable = ActionPolicyOverridable,
            OnFailure = onFailure,
            LoadShed = loadShed,
            SignatureCache = signatureCache,
        };
    }
}

/// <summary>
///     JSON-bindable shape for <see cref="LoadShedOptions"/>. Kept as a mutable class
///     with simple setters so it works with <c>IConfiguration.Bind</c>.
/// </summary>
public sealed class LoadShedDef
{
    /// <summary>Fraction of requests to shed when load band is High. Range 0.0 to 1.0. Default 0.2.</summary>
    public double DropFractionAtHigh { get; set; } = 0.2;

    /// <summary>Fraction of requests to shed when load band is Critical. Range 0.0 to 1.0. Default 0.5.</summary>
    public double DropFractionAtCritical { get; set; } = 0.5;
}

/// <summary>
///     JSON-bindable shape for <see cref="SignatureCacheOptions"/>. Mutable class with
///     simple setters so it works with <c>IConfiguration.Bind</c>.
/// </summary>
public sealed class SignatureCacheDef
{
    /// <summary>Whether the SignatureVerdictGate is enabled for this policy. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Minimum confidence required to skip the pipeline entirely. Default 0.85.</summary>
    public double SkipMinConfidence { get; set; } = 0.85;

    /// <summary>Maximum age in seconds for a Skip-eligible verdict. Default 300 (5 minutes).</summary>
    public int SkipMaxAgeSeconds { get; set; } = 300;

    /// <summary>Minimum confidence required to inject a prior bias. Default 0.30.</summary>
    public double BiasMinConfidence { get; set; } = 0.30;

    /// <summary>Maximum age in seconds for a Bias-eligible verdict. Default 86400 (24h).</summary>
    public int BiasMaxAgeSeconds { get; set; } = 86_400;

    /// <summary>
    ///     Fraction of Skip-eligible requests that nevertheless run the pipeline so the
    ///     verdict cache stays honest. Default 0.05 (5 percent). Set to 0 to disable.
    /// </summary>
    public double SkipSamplingRate { get; set; } = 0.05;

    /// <summary>Variance watchdog sensitivities. Defaults are appropriate for general-purpose sites.</summary>
    public WatchdogDef? Watchdog { get; set; }
}

/// <summary>
///     JSON-bindable shape for <see cref="VarianceWatchdogOptions"/>. Mutable class with
///     simple setters so it works with <c>IConfiguration.Bind</c>.
/// </summary>
public sealed class WatchdogDef
{
    /// <summary>Master switch. Default true.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Trip when the same primary signature appears from a new /24 within this many seconds. 0 to disable.</summary>
    public int IpRotationWindowSeconds { get; set; } = 300;

    /// <summary>Trip when the requested path's RequestState is not in the fingerprint's expected centroid set. Default true. (Follow-up; not implemented in v1.)</summary>
    public bool CheckPathCentroid { get; set; } = true;

    /// <summary>Trip when this fingerprint's recent request rate exceeds rolling mean by this multiplier. Default 10x. 0 to disable.</summary>
    public double RateSpikeMultiplier { get; set; } = 10.0;
}

/// <summary>
///     Configuration for a policy transition.
///     Transitions allow dynamic policy selection based on detection results.
/// </summary>
/// <remarks>
///     <para>
///         Transitions are evaluated in order. The first matching transition is executed.
///         A transition can either:
///         <list type="bullet">
///             <item>Execute an action policy (via <see cref="ActionPolicyName" />)</item>
///             <item>Execute a simple action (via <see cref="Action" />)</item>
///             <item>Chain to another detection policy (via <see cref="GoTo" />)</item>
///         </list>
///     </para>
///     <para>
///         Condition types (evaluated in this order):
///         <list type="number">
///             <item><see cref="WhenSignal" /> - Signal-based (e.g., "VerifiedGoodBot")</item>
///             <item><see cref="WhenReputationState" /> - Reputation-based (e.g., "ConfirmedBad")</item>
///             <item><see cref="WhenRiskExceeds" /> - Risk threshold exceeded</item>
///             <item><see cref="WhenRiskBelow" /> - Risk threshold not reached</item>
///         </list>
///     </para>
/// </remarks>
/// <example>
///     <code>
///     "Transitions": [
///       { "WhenRiskExceeds": 0.9, "ActionPolicyName": "block-hard" },
///       { "WhenRiskExceeds": 0.7, "ActionPolicyName": "challenge" },
///       { "WhenRiskExceeds": 0.5, "ActionPolicyName": "throttle" },
///       { "WhenRiskBelow": 0.3, "ActionPolicyName": "logonly" },
///       { "WhenSignal": "VerifiedGoodBot", "Action": "Allow" }
///     ]
///     </code>
/// </example>
public class TransitionConfig
{
    /// <summary>
    ///     Transition when risk score exceeds this value.
    ///     Range: 0.0-1.0
    /// </summary>
    public double? WhenRiskExceeds { get; set; }

    /// <summary>
    ///     Transition when risk score is below this value.
    ///     Range: 0.0-1.0
    /// </summary>
    public double? WhenRiskBelow { get; set; }

    /// <summary>
    ///     Transition when this signal is present in the blackboard.
    ///     Example: "VerifiedGoodBot", "HighRiskPattern", "DatacenterIp"
    /// </summary>
    public string? WhenSignal { get; set; }

    /// <summary>
    ///     Transition when this signal has a specific value.
    /// </summary>
    public object? WhenSignalValue { get; set; }

    /// <summary>
    ///     Transition when reputation state matches.
    ///     Values: "Neutral", "Suspect", "ConfirmedBad", "Trusted"
    /// </summary>
    public string? WhenReputationState { get; set; }

    /// <summary>
    ///     Detection policy to transition to (for chaining policies).
    /// </summary>
    public string? GoTo { get; set; }

    /// <summary>
    ///     Action type to take (Allow, Block, Challenge, Throttle, etc.)
    ///     Prefer ActionPolicyName for more control.
    ///     Values: Continue, Allow, Block, Challenge, Throttle, LogOnly, EscalateToSlowPath, EscalateToAi
    /// </summary>
    public string? Action { get; set; }

    /// <summary>
    ///     Name of the action policy to execute when this transition matches.
    ///     Takes precedence over Action if both are specified.
    ///     Reference any named action policy (built-in or custom).
    /// </summary>
    public string? ActionPolicyName { get; set; }

    /// <summary>
    ///     Description of this transition for logging/debugging.
    /// </summary>
    public string? Description { get; set; }

    /// <summary>
    ///     Converts this configuration to a PolicyTransition record.
    /// </summary>
    public PolicyTransition ToTransition()
    {
        DetectionPolicyAction? action = null;
        if (!string.IsNullOrEmpty(Action) && Enum.TryParse<DetectionPolicyAction>(Action, true, out var parsedAction))
            action = parsedAction;

        return new PolicyTransition
        {
            WhenRiskExceeds = WhenRiskExceeds,
            WhenRiskBelow = WhenRiskBelow,
            WhenSignal = WhenSignal,
            WhenSignalValue = WhenSignalValue,
            WhenReputationState = WhenReputationState,
            GoToPolicy = GoTo,
            Action = action,
            ActionPolicyName = ActionPolicyName,
            Description = Description
        };
    }
}