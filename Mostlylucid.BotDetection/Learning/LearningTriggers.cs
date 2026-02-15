using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Learning;

/// <summary>
///     Signal-triggered learning workflows.
///     Architecture:
///     - Detectors raise signals during detection (e.g., "ua.bot_pattern_detected")
///     - Triggers monitor signals and submit learning tasks to LearningCoordinator
///     - Learning happens asynchronously, off the request path
///     Example flow:
///     1. UserAgentContributor detects "HeadlessChrome" pattern
///     2. Raises signal: "ua.headless_detected" with confidence=0.9
///     3. Trigger sees high-confidence signal -> submits learning task to coordinator
///     4. Coordinator queues task under key "ua.pattern"
///     5. UserAgentPatternLearningHandler processes task asynchronously
///     6. Pattern is added to fast-path database for future requests
/// </summary>
public interface ILearningTrigger
{
    /// <summary>
    ///     Signals this trigger monitors for.
    /// </summary>
    IReadOnlySet<string> MonitoredSignals { get; }

    /// <summary>
    ///     Check if the signal should trigger learning.
    /// </summary>
    bool ShouldTrigger(BlackboardState state, string signal, object? signalValue);

    /// <summary>
    ///     Create learning task(s) from the triggered signal.
    /// </summary>
    IEnumerable<(string signalKey, LearningTask task)> CreateLearningTasks(
        BlackboardState state,
        string signal,
        object? signalValue);
}

/// <summary>
///     Trigger for high-confidence User-Agent pattern learning.
/// </summary>
public class UserAgentPatternTrigger : ILearningTrigger
{
    private readonly ILogger<UserAgentPatternTrigger> _logger;

    public UserAgentPatternTrigger(ILogger<UserAgentPatternTrigger> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<string> MonitoredSignals => new HashSet<string>
    {
        "ua.bot_probability",
        "ua.pattern_match",
        "ua.headless_detected",
        "ua.automation_detected"
    };

    public bool ShouldTrigger(BlackboardState state, string signal, object? signalValue)
    {
        // Specific pattern matches are high-confidence quick matches — always trigger
        if (signal is "ua.pattern_match" or "ua.headless_detected" or "ua.automation_detected") return true;

        // High bot probability for UA signal — but gate on detection confidence
        if (signal == "ua.bot_probability" && signalValue is double prob)
            return prob >= 0.85 && state.DetectionConfidence >= 0.7;

        return false;
    }

    public IEnumerable<(string signalKey, LearningTask task)> CreateLearningTasks(
        BlackboardState state,
        string signal,
        object? signalValue)
    {
        var userAgent = state.HttpContext.Request.Headers.UserAgent.ToString();
        if (string.IsNullOrEmpty(userAgent))
            yield break;

        // Extract pattern from user agent
        var pattern = ExtractPattern(userAgent);
        if (string.IsNullOrEmpty(pattern))
            yield break;

        // Use detection confidence for learning; fall back to risk score for pattern matches
        // (specific pattern matches like "ua.headless_detected" are inherently high-confidence)
        var confidence = signal is "ua.pattern_match" or "ua.headless_detected" or "ua.automation_detected"
            ? Math.Max(state.DetectionConfidence, 0.9) // Direct matches are very confident
            : state.DetectionConfidence;

        _logger.LogDebug(
            "UA pattern trigger fired: signal={Signal}, pattern={Pattern}, confidence={Confidence:F2}",
            signal, pattern, confidence);

        yield return ("ua.pattern", new LearningTask
        {
            Source = nameof(UserAgentPatternTrigger),
            OperationType = LearningOperationType.PatternExtraction,
            Pattern = pattern,
            Confidence = confidence,
            RequestId = state.RequestId,
            Metadata = new Dictionary<string, object>
            {
                ["signal"] = signal,
                ["user_agent"] = userAgent,
                ["full_ua_length"] = userAgent.Length
            }
        });
    }

    private string? ExtractPattern(string userAgent)
    {
        // Extract meaningful pattern from UA string
        // Examples:
        // "Mozilla/5.0 ... HeadlessChrome/120.0.0.0" -> "HeadlessChrome"
        // "curl/8.4.0" -> "curl/"
        // "Googlebot/2.1" -> "Googlebot/"

        if (userAgent.Contains("HeadlessChrome", StringComparison.OrdinalIgnoreCase))
            return "HeadlessChrome";

        if (userAgent.StartsWith("curl/", StringComparison.OrdinalIgnoreCase))
            return "curl/";

        if (userAgent.Contains("Googlebot", StringComparison.OrdinalIgnoreCase))
            return "Googlebot/";

        if (userAgent.Contains("Puppeteer", StringComparison.OrdinalIgnoreCase))
            return "Puppeteer";

        if (userAgent.Contains("Selenium", StringComparison.OrdinalIgnoreCase))
            return "Selenium";

        if (userAgent.Contains("PhantomJS", StringComparison.OrdinalIgnoreCase))
            return "PhantomJS";

        // Generic automation indicators
        if (userAgent.Contains("bot", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("spider", StringComparison.OrdinalIgnoreCase) ||
            userAgent.Contains("crawler", StringComparison.OrdinalIgnoreCase))
        {
            // Extract first word
            var firstSpace = userAgent.IndexOf(' ');
            return firstSpace > 0 ? userAgent[..firstSpace] : userAgent;
        }

        return null;
    }
}

/// <summary>
///     Trigger for heuristic weight learning based on high-confidence detections.
/// </summary>
public class HeuristicWeightTrigger : ILearningTrigger
{
    private readonly ILogger<HeuristicWeightTrigger> _logger;

    public HeuristicWeightTrigger(ILogger<HeuristicWeightTrigger> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<string> MonitoredSignals => new HashSet<string>
    {
        "detection.high_confidence",
        "detection.completed",
        "user.feedback_received"
    };

    public bool ShouldTrigger(BlackboardState state, string signal, object? signalValue)
    {
        // Always trigger on user feedback (ground truth)
        if (signal == "user.feedback_received") return true;

        // Trigger on high-confidence detections (for online learning — learn from certain verdicts)
        if (signal == "detection.high_confidence")
            return state.DetectionConfidence >= 0.85;

        // Key learning trigger: high bot probability BUT low confidence.
        // This means "we think it's a bot but aren't sure" — run full learning pipeline
        // to gather more evidence and improve future detection.
        if (signal == "detection.completed")
        {
            // High probability, low confidence = uncertain detection → learn
            if (state.CurrentRiskScore >= 0.5 && state.DetectionConfidence < 0.7)
                return true;

            // Also learn from very high-confidence detections (training data)
            if (state.DetectionConfidence >= 0.85)
                return true;
        }

        return false;
    }

    public IEnumerable<(string signalKey, LearningTask task)> CreateLearningTasks(
        BlackboardState state,
        string signal,
        object? signalValue)
    {
        // Extract features from state
        var features = ExtractFeatures(state);
        if (features.Count == 0)
        {
            _logger.LogWarning("No features extracted from state for heuristic learning");
            yield break;
        }

        // Determine label (bot = true, human = false)
        bool label;
        if (signal == "user.feedback_received" && signalValue is bool feedbackLabel)
        {
            // Use user feedback as ground truth
            label = feedbackLabel;
            _logger.LogInformation("User feedback received: isBot={Label}", label);
        }
        else
        {
            // Use detection result as label (high-confidence detections only)
            label = state.CurrentRiskScore >= 0.5;
        }

        // Determine operation type based on confidence
        var isUncertain = state.DetectionConfidence < 0.7 && state.CurrentRiskScore >= 0.5;
        var operationType = signal == "user.feedback_received"
            ? LearningOperationType.WeightUpdate
            : isUncertain
                ? LearningOperationType.ModelTraining // Uncertain → full training run
                : LearningOperationType.PatternUpdate; // Certain → reinforce existing patterns

        yield return ("heuristic.weights", new LearningTask
        {
            Source = nameof(HeuristicWeightTrigger),
            OperationType = operationType,
            Features = features,
            Label = label,
            Confidence = state.DetectionConfidence,
            RequestId = state.RequestId,
            Metadata = new Dictionary<string, object>
            {
                ["signal"] = signal,
                ["risk_score"] = state.CurrentRiskScore,
                ["detection_confidence"] = state.DetectionConfidence,
                ["detector_count"] = state.CompletedDetectors.Count,
                ["uncertain"] = isUncertain
            }
        });
    }

    private Dictionary<string, double> ExtractFeatures(BlackboardState state)
    {
        var features = new Dictionary<string, double>();

        // Extract features from signals
        foreach (var (key, value) in state.Signals)
            // Convert signal values to features
            if (value is double d)
                features[key] = d;
            else if (value is bool b)
                features[key] = b ? 1.0 : 0.0;
            else if (value is int i) features[key] = i;

        // Add derived features
        features["detector_count"] = state.CompletedDetectors.Count;
        features["risk_score"] = state.CurrentRiskScore;
        features["detection_confidence"] = state.DetectionConfidence;
        features["processing_time_ms"] = state.Elapsed.TotalMilliseconds;

        return features;
    }
}

/// <summary>
///     Trigger for TLS fingerprint pattern learning.
/// </summary>
public class TlsFingerprintTrigger : ILearningTrigger
{
    private readonly ILogger<TlsFingerprintTrigger> _logger;

    public TlsFingerprintTrigger(ILogger<TlsFingerprintTrigger> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<string> MonitoredSignals => new HashSet<string>
    {
        "tls.ja3_hash",
        "tls.ja4_hash",
        "tls.fingerprint_match",
        "tls.unknown_fingerprint"
    };

    public bool ShouldTrigger(BlackboardState state, string signal, object? signalValue)
    {
        // Trigger on unknown fingerprints when we have reasonable confidence in the overall detection
        if (signal == "tls.unknown_fingerprint")
            return state.CurrentRiskScore >= 0.7 && state.DetectionConfidence >= 0.5;

        // Trigger on fingerprint matches for confidence updates
        if (signal == "tls.fingerprint_match") return true;

        return false;
    }

    public IEnumerable<(string signalKey, LearningTask task)> CreateLearningTasks(
        BlackboardState state,
        string signal,
        object? signalValue)
    {
        var fingerprint = signalValue?.ToString();
        if (string.IsNullOrEmpty(fingerprint))
            yield break;

        var operationType = signal == "tls.unknown_fingerprint"
            ? LearningOperationType.PatternExtraction
            : LearningOperationType.PatternUpdate;

        yield return ("tls.ja3", new LearningTask
        {
            Source = nameof(TlsFingerprintTrigger),
            OperationType = operationType,
            Pattern = fingerprint,
            Confidence = state.DetectionConfidence,
            RequestId = state.RequestId,
            Metadata = new Dictionary<string, object>
            {
                ["signal"] = signal,
                ["fingerprint_type"] = signal.Contains("ja3") ? "ja3" : "ja4"
            }
        });
    }
}

/// <summary>
///     Service that monitors blackboard signals and triggers learning workflows.
/// </summary>
public class LearningTriggerService
{
    private readonly ILearningCoordinator _coordinator;
    private readonly ILogger<LearningTriggerService> _logger;
    private readonly IEnumerable<ILearningTrigger> _triggers;

    public LearningTriggerService(
        ILearningCoordinator coordinator,
        IEnumerable<ILearningTrigger> triggers,
        ILogger<LearningTriggerService> logger)
    {
        _coordinator = coordinator;
        _triggers = triggers;
        _logger = logger;
    }

    /// <summary>
    ///     Check all signals in the blackboard and trigger learning workflows if conditions are met.
    ///     Called after detection completes (in response path or after request).
    /// </summary>
    public void ProcessSignals(BlackboardState state)
    {
        foreach (var (signal, value) in state.Signals)
        {
            // Find triggers monitoring this signal
            var relevantTriggers = _triggers
                .Where(t => t.MonitoredSignals.Contains(signal))
                .ToList();

            foreach (var trigger in relevantTriggers)
                try
                {
                    if (!trigger.ShouldTrigger(state, signal, value))
                        continue;

                    // Create and submit learning tasks
                    var learningTasks = trigger.CreateLearningTasks(state, signal, value);

                    foreach (var (signalKey, task) in learningTasks)
                        if (!_coordinator.TrySubmitLearning(signalKey, task))
                            _logger.LogWarning(
                                "Failed to submit learning task: signalKey={SignalKey}, trigger={Trigger}",
                                signalKey, trigger.GetType().Name);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "Error processing signal {Signal} with trigger {Trigger}",
                        signal, trigger.GetType().Name);
                }
        }
    }
}