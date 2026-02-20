using System.Collections.Immutable;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Policies;

/// <summary>
///     A detection policy defines a workflow for bot detection.
///     Policies specify which detectors run, in what order, and when to transition to other policies.
/// </summary>
public sealed record DetectionPolicy
{
    /// <summary>Unique name of this policy (e.g., "default", "loginStrict", "apiRelaxed")</summary>
    public required string Name { get; init; }

    /// <summary>Human-readable description of what this policy is for</summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Fast path detectors - run synchronously, expected to complete quickly (&lt;100ms).
    ///     These run in Wave 0 of the blackboard.
    /// </summary>
    public ImmutableList<string> FastPathDetectors { get; init; } = [];

    /// <summary>
    ///     Slow path detectors - run asynchronously when fast path is inconclusive.
    ///     These run in subsequent waves and may include expensive analysis.
    /// </summary>
    public ImmutableList<string> SlowPathDetectors { get; init; } = [];

    /// <summary>
    ///     AI/ML detectors - only run when escalated by other detectors or risk threshold.
    /// </summary>
    public ImmutableList<string> AiPathDetectors { get; init; } = [];

    /// <summary>
    ///     Response path detectors - run AFTER the response is generated (post-request analysis).
    ///     These detectors analyze response patterns, honeypot interactions, error harvesting, etc.
    ///     Results feed back into future request detections via ResponseBehaviorContributor.
    ///     Examples: ResponseCoordinator, HoneypotAnalyzer, ErrorPatternDetector
    /// </summary>
    public ImmutableList<string> ResponsePathDetectors { get; init; } = [];

    /// <summary>
    ///     Whether to use the fast path at all. Set to false for always-deep analysis.
    /// </summary>
    public bool UseFastPath { get; init; } = true;

    /// <summary>
    ///     Force slow path to run even if fast path is conclusive.
    ///     Useful for high-security endpoints.
    /// </summary>
    public bool ForceSlowPath { get; init; }

    /// <summary>
    ///     Whether to escalate to AI detectors when risk exceeds AiEscalationThreshold.
    /// </summary>
    public bool EscalateToAi { get; init; }

    /// <summary>
    ///     Risk threshold above which to escalate to AI detectors.
    ///     Default: 0.6 (60% confidence of being a bot).
    /// </summary>
    public double AiEscalationThreshold { get; init; } = 0.6;

    /// <summary>
    ///     Risk threshold below which to allow early exit from fast path.
    ///     Default: 0.3 (30% confidence = likely human).
    /// </summary>
    public double EarlyExitThreshold { get; init; } = 0.3;

    /// <summary>
    ///     Risk threshold above which to block immediately without further analysis.
    ///     Default: 0.95 (95% confidence = definitely a bot).
    /// </summary>
    public double ImmediateBlockThreshold { get; init; } = 0.95;

    /// <summary>
    ///     Minimum confidence required before any blocking decision takes effect.
    ///     Even if bot probability exceeds <see cref="ImmediateBlockThreshold" />,
    ///     blocking only occurs when confidence meets this gate.
    ///     This prevents low-evidence verdicts from triggering blocks.
    ///     Default: 0.0 (no confidence gate — backwards compatible).
    /// </summary>
    public double MinConfidence { get; init; }

    /// <summary>
    ///     Weight overrides for detectors in this policy.
    ///     Key = detector name, Value = weight multiplier.
    /// </summary>
    public ImmutableDictionary<string, double> WeightOverrides { get; init; } =
        ImmutableDictionary<string, double>.Empty;

    /// <summary>
    ///     Transitions to other policies based on conditions.
    /// </summary>
    public ImmutableList<PolicyTransition> Transitions { get; init; } = [];

    /// <summary>
    ///     Maximum time allowed for this policy's detection pipeline.
    /// </summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>
    ///     Whether this policy is enabled. Disabled policies are skipped.
    /// </summary>
    public bool Enabled { get; init; } = true;

    /// <summary>
    ///     When true, all detectors run in Wave 0 regardless of their TriggerConditions.
    ///     Primary use case: learning/slow path where full characterization is required.
    ///     Also useful for demo/testing to ensure the full detection pipeline runs.
    ///     Default: false (respect trigger conditions for optimal performance).
    /// </summary>
    public bool BypassTriggerConditions { get; init; }

    /// <summary>
    ///     Whether API keys can override the action policy for this detection policy.
    ///     When false, the policy's ActionPolicyName and transitions take precedence
    ///     over any API key ActionPolicyName override.
    ///     Default: true (API keys can override).
    /// </summary>
    public bool ActionPolicyOverridable { get; init; } = true;

    /// <summary>
    ///     Detectors to exclude from this policy's pipeline.
    ///     Populated by API key overlays to selectively disable detectors per key.
    ///     Detectors whose name appears in this set are skipped during orchestration.
    /// </summary>
    public ImmutableHashSet<string> ExcludedDetectors { get; init; } =
        ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Creates a new policy with API key overlay applied (excluded detectors + weight overrides).
    /// </summary>
    public DetectionPolicy WithApiKeyOverlay(ApiKeyContext keyCtx)
    {
        var mergedWeights = WeightOverrides;
        if (keyCtx.WeightOverrides.Count > 0)
        {
            var builder = mergedWeights.ToBuilder();
            foreach (var kv in keyCtx.WeightOverrides)
                builder[kv.Key] = kv.Value;
            mergedWeights = builder.ToImmutable();
        }

        return this with
        {
            Name = $"{Name}+apikey:{keyCtx.KeyName}",
            ExcludedDetectors = keyCtx.DisabledDetectors.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            WeightOverrides = mergedWeights
        };
    }

    /// <summary>
    ///     Creates the default policy - a sensible baseline that can be overridden via JSON config.
    ///     This is a FALLBACK - define your policy in appsettings.json for full control:
    ///     "Policies": {
    ///     "default": {
    ///     "FastPath": ["UserAgent", "Header", "Ip", "Behavioral", "ClientSide", "Inconsistency"],
    ///     "AiPath": ["Onnx"],  // Add any AI detectors here for sync decision
    ///     "EscalateToAi": true,
    ///     "AiEscalationThreshold": 0.0,
    ///     "EarlyExitThreshold": 0.85,
    ///     "ImmediateBlockThreshold": 0.95
    ///     }
    ///     }
    ///     LEARNING: Async background service queues uncertain requests for full AI analysis.
    /// </summary>
    public static DetectionPolicy Default => new()
    {
        Name = "default",
        Description = "Fast path with early bailout and reputation cache",
        // All static/fast detectors - includes reputation for instant allow/block
        // Wave 0 (no triggers): FastPathReputation, TimescaleReputation, UserAgent, Header, Ip, SecurityTool, Behavioral, CacheBehavior
        // Wave 1 (trigger on ua.raw): Inconsistency, VersionAge, Heuristic, HeuristicLate, ReputationBias
        // Wave 1+ (trigger on fingerprint): ClientSide
        FastPathDetectors =
        [
            "FastPathReputation", "TimescaleReputation", "UserAgent", "Header", "Ip", "SecurityTool", "Behavioral", "ClientSide",
            "CacheBehavior", "Inconsistency", "VersionAge", "Heuristic", "HeuristicLate", "ReputationBias", "Geo", "GeoClient"
        ],
        SlowPathDetectors = [],
        AiPathDetectors = [], // Empty by default - add ONNX/LLM/others via JSON config
        ResponsePathDetectors = ["ResponseBehavior"], // Track response patterns for learning
        UseFastPath = true,
        ForceSlowPath = false,
        EscalateToAi = false, // Off by default - enable via JSON with AI detectors
        // Early bailout thresholds for performance
        EarlyExitThreshold = 0.3,
        ImmediateBlockThreshold = 0.95
    };

    /// <summary>
    ///     Creates a demo policy that runs ALL registered detectors for full visibility.
    ///     Use this for testing and demonstration purposes to see the full pipeline.
    /// </summary>
    public static DetectionPolicy Demo => new()
    {
        Name = "demo",
        Description = "Full detection pipeline for demonstration - runs all detectors",
        FastPathDetectors = [], // Empty = run all registered detectors
        SlowPathDetectors = [],
        AiPathDetectors = [],
        UseFastPath = false, // Force full analysis
        ForceSlowPath = true,
        EscalateToAi = true,
        AiEscalationThreshold = 0.0, // Always escalate to AI for demo
        EarlyExitThreshold = 0.0, // Never early exit
        ImmediateBlockThreshold = 1.0, // Never block immediately
        BypassTriggerConditions = true // Run ALL detectors in Wave 0, ignore triggers
    };

    /// <summary>
    ///     Creates a strict policy for high-security endpoints (login, payment, admin).
    /// </summary>
    public static DetectionPolicy Strict => new()
    {
        Name = "strict",
        Description = "High-security detection with deep analysis",
        FastPathDetectors = ["UserAgent", "Header", "Ip"],
        SlowPathDetectors = ["Behavioral", "Inconsistency", "ClientSide"],
        AiPathDetectors = ["Onnx", "Llm"],
        UseFastPath = true,
        ForceSlowPath = true,
        EscalateToAi = true,
        AiEscalationThreshold = 0.4,
        ImmediateBlockThreshold = 0.9,
        WeightOverrides = new Dictionary<string, double>
        {
            ["Behavioral"] = 2.0,
            ["Inconsistency"] = 2.0
        }.ToImmutableDictionary()
    };

    /// <summary>
    ///     Creates a relaxed policy for public/static content.
    /// </summary>
    public static DetectionPolicy Relaxed => new()
    {
        Name = "relaxed",
        Description = "Minimal detection for public content",
        FastPathDetectors = ["UserAgent"],
        SlowPathDetectors = [],
        AiPathDetectors = [],
        UseFastPath = true,
        EarlyExitThreshold = 0.5,
        ImmediateBlockThreshold = 0.99
    };

    /// <summary>
    ///     Creates a static asset policy - EXTREMELY permissive for JS/CSS/images/fonts.
    ///     Static assets are often requested rapidly by browsers (webpack bundles, etc.)
    ///     which can trigger false positives in behavioral detection.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This policy uses absolute minimal detection to avoid false positives:
    ///         <list type="bullet">
    ///             <item><b>FastPathReputation</b> - Only IP reputation (blocks known malicious IPs)</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         EXCLUDES all other detectors:
    ///         <list type="bullet">
    ///             <item><b>UserAgent</b> - Excluded (legitimate tools/CDNs may use custom UAs)</item>
    ///             <item><b>Header</b> - Excluded (CDNs/proxies modify headers)</item>
    ///             <item><b>Behavioral</b> - Excluded (rapid parallel requests are normal)</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Thresholds are EXTREMELY permissive - essentially never blocks except known malicious IPs.
    ///         Early exit at 0.95 means almost all requests pass immediately.
    ///         Block threshold is 0.999 (99.9%) - only blocks absolutely confirmed threats.
    ///     </para>
    ///     <para>
    ///         Philosophy: Static assets should be freely accessible like a CDN. Only block
    ///         IPs that are known to be malicious (DDoS sources, compromised hosts, etc.).
    ///     </para>
    /// </remarks>
    public static DetectionPolicy Static => new()
    {
        Name = "static",
        Description = "Extremely permissive policy for static assets - CDN-like behavior",
        // ONLY FastPathReputation - just check if IP is known malicious
        // No UA, no headers, no behavioral analysis
        FastPathDetectors = ["FastPathReputation"],
        SlowPathDetectors = [],
        AiPathDetectors = [],
        UseFastPath = true,
        ForceSlowPath = false,
        EscalateToAi = false,
        EarlyExitThreshold = 0.95, // Almost always exit early (95%+ confidence required to continue)
        ImmediateBlockThreshold = 0.999, // 99.9% confidence required to block
        WeightOverrides = new Dictionary<string, double>
        {
            // If any detector somehow runs, reduce all weights to near-zero
            ["UserAgent"] = 0.01,
            ["Header"] = 0.01,
            ["Behavioral"] = 0.01,
            ["Ip"] = 0.01,
            ["ClientSide"] = 0.01,
            ["Inconsistency"] = 0.01,
            // Only FastPathReputation at full weight
            ["FastPathReputation"] = 1.0
        }.ToImmutableDictionary()
    };

    /// <summary>
    ///     Creates a policy that allows verified bots (search engines, social media).
    /// </summary>
    public static DetectionPolicy AllowVerifiedBots => new()
    {
        Name = "allowVerifiedBots",
        Description = "Allow verified good bots through",
        FastPathDetectors = ["UserAgent", "Header"],
        SlowPathDetectors = [],
        AiPathDetectors = [],
        UseFastPath = true,
        Transitions =
        [
            PolicyTransition.OnSignal("VerifiedGoodBot", PolicyAction.Allow)
        ]
    };

    /// <summary>
    ///     Creates the LEARNING policy - used by the async learning background service.
    ///     When uncertain requests exceed threshold, they're queued and processed with this policy.
    ///     Runs ALL detectors including ONNX + LLM to gather maximum signal for training.
    ///     Does NOT block - logs only for pattern learning and model improvement.
    /// </summary>
    public static DetectionPolicy Learning => new()
    {
        Name = "learning",
        Description = "Async learning - full pipeline with ONNX + LLM for new/uncertain patterns",
        FastPathDetectors = [], // Empty = run ALL registered fast detectors
        SlowPathDetectors = [], // Empty = run ALL registered slow detectors
        AiPathDetectors = [], // Empty = run ALL registered AI detectors (ONNX + LLM)
        UseFastPath = false, // Don't use fast path shortcuts
        ForceSlowPath = true, // Always run slow path
        EscalateToAi = true, // Always run AI (ONNX + LLM)
        AiEscalationThreshold = 0.0, // Always escalate - learn from everything
        EarlyExitThreshold = 0.0, // Never early exit - always get full picture
        ImmediateBlockThreshold = 1.0, // Never block - learning mode
        BypassTriggerConditions = true, // Run all detectors regardless of triggers
        Timeout = TimeSpan.FromSeconds(10) // Allow more time for thorough analysis
    };

    /// <summary>
    ///     Creates the YARP LEARNING policy - optimized for gateway learning mode.
    ///     Runs ALL detectors EXCEPT LLM (for performance in high-traffic scenarios).
    ///     Collects comprehensive signatures including all signals and detector outputs.
    ///     Does NOT block - shadow mode for training data collection.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Differences from standard Learning policy:
    ///         <list type="bullet">
    ///             <item>EXCLUDES LLM detector for better gateway performance</item>
    ///             <item>Includes ONNX for ML-based signals</item>
    ///             <item>Shorter timeout (5s vs 10s) for high-traffic scenarios</item>
    ///             <item>Designed for YARP reverse proxy use cases</item>
    ///         </list>
    ///     </para>
    ///     <para>
    ///         Enable via configuration:
    ///         <code>
    ///         "YarpLearningMode": {
    ///           "Enabled": true,
    ///           "OutputPath": "./yarp-learning-data",
    ///           "LogToConsole": true,
    ///           "IncludeLlmDetector": false
    ///         }
    ///         </code>
    ///     </para>
    /// </remarks>
    public static DetectionPolicy YarpLearning => new()
    {
        Name = "yarp-learning",
        Description = "YARP gateway learning - full pipeline without LLM for training data collection",
        FastPathDetectors = [], // Empty = run ALL registered fast detectors
        SlowPathDetectors = [], // Empty = run ALL registered slow detectors
        AiPathDetectors = ["Onnx"], // Only ONNX, skip LLM for performance
        UseFastPath = false, // Don't use fast path shortcuts
        ForceSlowPath = true, // Always run slow path
        EscalateToAi = true, // Run ONNX
        AiEscalationThreshold = 0.0, // Always escalate - learn from everything
        EarlyExitThreshold = 0.0, // Never early exit - always get full picture
        ImmediateBlockThreshold = 1.0, // Never block - learning mode
        BypassTriggerConditions = true, // Run all detectors regardless of triggers
        Timeout = TimeSpan.FromSeconds(5) // Shorter timeout for gateway performance
    };

    /// <summary>
    ///     Creates a monitor policy that runs silently without blocking.
    ///     Use this to observe traffic patterns before enabling enforcement.
    ///     Fast path runs, escalates to AI for uncertain cases, but never blocks.
    /// </summary>
    public static DetectionPolicy Monitor => new()
    {
        Name = "monitor",
        Description = "Shadow mode - detect and log, never block",
        FastPathDetectors = ["UserAgent", "Header", "Ip", "Behavioral"],
        SlowPathDetectors = ["Inconsistency", "ClientSide"],
        AiPathDetectors = ["Onnx"], // Use fast ONNX, not LLM
        UseFastPath = true,
        EscalateToAi = true,
        AiEscalationThreshold = 0.5, // Escalate uncertain cases
        EarlyExitThreshold = 0.2, // Early exit for clear humans
        ImmediateBlockThreshold = 1.0 // Never block - monitor only
    };

    /// <summary>
    ///     Creates an API policy optimized for API endpoints.
    ///     Focuses on behavioral and header analysis, minimal latency.
    /// </summary>
    public static DetectionPolicy Api => new()
    {
        Name = "api",
        Description = "Optimized for API endpoints - low latency, behavioral focus",
        FastPathDetectors = ["UserAgent", "Header", "Ip", "Behavioral"],
        SlowPathDetectors = [], // No slow path for API latency
        AiPathDetectors = ["Onnx"], // Fast ONNX only if escalated
        UseFastPath = true,
        EscalateToAi = true,
        AiEscalationThreshold = 0.7, // High threshold - minimize AI calls
        EarlyExitThreshold = 0.3,
        ImmediateBlockThreshold = 0.95,
        Timeout = TimeSpan.FromMilliseconds(100), // Tight timeout for API
        WeightOverrides = new Dictionary<string, double>
        {
            ["Behavioral"] = 2.0 // Weight behavioral heavily for API
        }.ToImmutableDictionary()
    };

    /// <summary>
    ///     Creates a fast path policy WITH ONNX for decision-only (inference).
    ///     Uses ONNX for real-time decisions but does NOT trigger learning.
    ///     Good for production when you want ML decisions without learning overhead.
    /// </summary>
    public static DetectionPolicy FastWithOnnx => new()
    {
        Name = "fast-onnx",
        Description = "Fast path + ONNX inference (decision-only, no learning)",
        FastPathDetectors = ["UserAgent", "Header", "Ip", "Behavioral", "ClientSide", "Inconsistency", "VersionAge"],
        SlowPathDetectors = [],
        AiPathDetectors = ["Onnx"], // ONNX for inference only
        UseFastPath = true,
        ForceSlowPath = false,
        EscalateToAi = true, // Run ONNX sync
        AiEscalationThreshold = 0.0, // Always run ONNX
        EarlyExitThreshold = 0.85,
        ImmediateBlockThreshold = 0.95
    };

    /// <summary>
    ///     Creates a fast path policy WITH ONNX + LLM for decision-only.
    ///     Uses both ONNX and LLM for real-time decisions but does NOT trigger learning.
    ///     Higher latency but maximum accuracy without learning overhead.
    /// </summary>
    public static DetectionPolicy FastWithAi => new()
    {
        Name = "fast-ai",
        Description = "Fast path + ONNX + LLM inference (decision-only, no learning)",
        FastPathDetectors = ["UserAgent", "Header", "Ip", "Behavioral", "ClientSide", "Inconsistency", "VersionAge"],
        SlowPathDetectors = [],
        AiPathDetectors = ["Onnx", "Llm"], // Both for inference only
        UseFastPath = true,
        ForceSlowPath = false,
        EscalateToAi = true, // Run AI sync
        AiEscalationThreshold = 0.0, // Always run AI
        EarlyExitThreshold = 0.85,
        ImmediateBlockThreshold = 0.95,
        Timeout = TimeSpan.FromSeconds(3) // Allow time for LLM
    };
}

/// <summary>
///     Defines a transition from one policy to another based on conditions.
/// </summary>
public sealed record PolicyTransition
{
    /// <summary>Transition when risk score exceeds this threshold</summary>
    public double? WhenRiskExceeds { get; init; }

    /// <summary>Transition when risk score falls below this threshold</summary>
    public double? WhenRiskBelow { get; init; }

    /// <summary>Transition when this signal is present in the blackboard</summary>
    public string? WhenSignal { get; init; }

    /// <summary>Transition when this signal has a specific value</summary>
    public object? WhenSignalValue { get; init; }

    /// <summary>Transition when reputation state matches</summary>
    public string? WhenReputationState { get; init; }

    /// <summary>Name of the policy to transition to</summary>
    public string? GoToPolicy { get; init; }

    /// <summary>Action to take instead of transitioning to another policy</summary>
    public PolicyAction? Action { get; init; }

    /// <summary>Description of this transition for logging/debugging</summary>
    public string? Description { get; init; }

    /// <summary>
    ///     Name of the action policy to execute when this transition matches.
    ///     Takes precedence over Action if both are specified.
    ///     Reference any named action policy (built-in or custom like "holodeck").
    /// </summary>
    public string? ActionPolicyName { get; init; }

    /// <summary>Creates a transition triggered by a signal</summary>
    public static PolicyTransition OnSignal(string signalKey, string goToPolicy)
    {
        return new PolicyTransition { WhenSignal = signalKey, GoToPolicy = goToPolicy };
    }

    /// <summary>Creates a transition triggered by a signal that takes an action</summary>
    public static PolicyTransition OnSignal(string signalKey, PolicyAction action)
    {
        return new PolicyTransition { WhenSignal = signalKey, Action = action };
    }

    /// <summary>Creates a transition triggered by high risk</summary>
    public static PolicyTransition OnHighRisk(double threshold, string goToPolicy)
    {
        return new PolicyTransition { WhenRiskExceeds = threshold, GoToPolicy = goToPolicy };
    }

    /// <summary>Creates a transition triggered by low risk</summary>
    public static PolicyTransition OnLowRisk(double threshold, string goToPolicy)
    {
        return new PolicyTransition { WhenRiskBelow = threshold, GoToPolicy = goToPolicy };
    }

    /// <summary>Creates a transition triggered by high risk that takes an action</summary>
    public static PolicyTransition OnHighRisk(double threshold, PolicyAction action)
    {
        return new PolicyTransition { WhenRiskExceeds = threshold, Action = action };
    }
}

/// <summary>
///     Actions that can be taken by a policy transition.
/// </summary>
public enum PolicyAction
{
    /// <summary>Continue with current policy</summary>
    Continue,

    /// <summary>Allow the request through immediately</summary>
    Allow,

    /// <summary>Block the request immediately</summary>
    Block,

    /// <summary>Challenge the user (CAPTCHA, proof of work, etc.)</summary>
    Challenge,

    /// <summary>Throttle/rate limit the request</summary>
    Throttle,

    /// <summary>Log and continue (shadow mode)</summary>
    LogOnly,

    /// <summary>Escalate to slow path</summary>
    EscalateToSlowPath,

    /// <summary>Escalate to AI detectors</summary>
    EscalateToAi
}