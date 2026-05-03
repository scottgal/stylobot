using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Events.Listeners;

/// <summary>
///     Listens to detection signals to capture learning opportunities.
///     Collects patterns for future ML training without blocking the hot path.
/// </summary>
public class LearningListener : IBotSignalListener, ISignalSubscriber
{
    private readonly ILogger<LearningListener> _logger;

    public LearningListener(ILogger<LearningListener> logger)
    {
        _logger = logger;
    }

    public ValueTask OnSignalAsync(
        BotSignalType signal,
        DetectionContext context,
        CancellationToken ct = default)
    {
        switch (signal)
        {
            case BotSignalType.InconsistencyUpdated:
                CaptureInconsistencyLearning(context);
                break;

            case BotSignalType.AiClassificationCompleted:
                CaptureAiLearning(context);
                break;

            case BotSignalType.Finalising:
                CaptureFinalLearning(context);
                break;
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Listen to all signals for learning
    /// </summary>
    public IEnumerable<BotSignalType> SubscribedSignals => new[]
    {
        BotSignalType.InconsistencyUpdated,
        BotSignalType.AiClassificationCompleted,
        BotSignalType.Finalising
    };

    private void CaptureInconsistencyLearning(DetectionContext context)
    {
        var inconsistencyScore = context.GetSignal<double>(SignalKeys.InconsistencyScore);
        var details = context.GetSignal<List<string>>(SignalKeys.InconsistencyDetails);

        if (inconsistencyScore > 0.7 && details?.Any() == true)
        {
            // High-confidence inconsistency - good learning signal
            context.AddLearning(new LearnedSignal
            {
                SourceDetector = "InconsistencyDetector",
                SignalType = "Inconsistency",
                Value = string.Join("; ", details),
                Confidence = inconsistencyScore,
                Metadata = new Dictionary<string, object>
                {
                    ["userAgent"] = context.HttpContext.Request.Headers.UserAgent.ToString(),
                    ["ip"] = context.HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"
                }
            });

            _logger.LogDebug("Captured inconsistency learning signal: {Details}", string.Join("; ", details));
        }
    }

    private void CaptureAiLearning(DetectionContext context)
    {
        var aiPrediction = TryReadAiPrediction(context.GetSignal<object>(SignalKeys.AiPrediction));
        var aiConfidence = TryReadDouble(context.GetSignal<object>(SignalKeys.AiConfidence));
        var learnedPattern = context.GetSignal<string>(SignalKeys.AiLearnedPattern);

        if (aiPrediction.HasValue && aiConfidence > 0.8 && !string.IsNullOrEmpty(learnedPattern))
        {
            context.AddLearning(new LearnedSignal
            {
                SourceDetector = "AiDetector",
                SignalType = "Pattern",
                Value = learnedPattern,
                Confidence = aiConfidence,
                Metadata = new Dictionary<string, object>
                {
                    ["prediction"] = aiPrediction.Value,
                    ["timestamp"] = DateTimeOffset.UtcNow
                }
            });

            _logger.LogDebug("Captured AI learning signal: pattern={Pattern}", learnedPattern);
        }
    }

    private void CaptureFinalLearning(DetectionContext context)
    {
        // At finalisation, check if this is a high-confidence detection
        // worth learning from
        var maxScore = context.MaxScore;

        if (maxScore > 0.85)
        {
            // High-confidence bot - capture full feature set for training
            var features = ExtractFeatureVector(context);

            context.AddLearning(new LearnedSignal
            {
                SourceDetector = "FinalAssessment",
                SignalType = "HighConfidenceDetection",
                Value = "bot",
                Confidence = maxScore,
                Metadata = new Dictionary<string, object>
                {
                    ["features"] = features,
                    ["scores"] = context.Scores.ToDictionary(k => k.Key, v => v.Value),
                    ["detectorCount"] = context.DetectorResults.Count
                }
            });

            _logger.LogDebug("Captured high-confidence detection for learning: score={Score:F2}", maxScore);
        }
    }

    private Dictionary<string, double> ExtractFeatureVector(DetectionContext context)
    {
        var features = new Dictionary<string, double>();

        // Collect all numeric signals as features
        foreach (var key in context.SignalKeys)
        {
            var value = context.GetSignal<object>(key);
            switch (value)
            {
                case double d:
                    features[key] = d;
                    break;
                case bool b:
                    features[key] = b ? 1.0 : 0.0;
                    break;
                case int i:
                    features[key] = i;
                    break;
            }
        }

        return features;
    }

    private static bool? TryReadAiPrediction(object? value)
    {
        return value switch
        {
            bool b => b,
            string s when s.Equals("bot", StringComparison.OrdinalIgnoreCase) => true,
            string s when s.Equals("human", StringComparison.OrdinalIgnoreCase) => false,
            string s when bool.TryParse(s, out var parsed) => parsed,
            int i => i != 0,
            long l => l != 0,
            double d => d > 0.5,
            float f => f > 0.5f,
            _ => null
        };
    }

    private static double TryReadDouble(object? value)
    {
        return value switch
        {
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            string s when double.TryParse(s, out var parsed) => parsed,
            _ => 0.0
        };
    }
}
