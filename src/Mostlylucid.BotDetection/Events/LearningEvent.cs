namespace Mostlylucid.BotDetection.Events;

/// <summary>
///     Categories of learning events raised on
///     <see cref="Mostlylucid.BotDetection.Learning.LearningCoordinator.Signals"/>.
///     Each type maps 1:1 to a
///     <see cref="Mostlylucid.BotDetection.Learning.LearningSignalKeys"/> entry;
///     consumers subscribe to the keys they care about rather than filtering
///     one blob-stream.
/// </summary>
public enum LearningEventType
{
    // ==========================================
    // Detection Events (from hot path)
    // ==========================================

    /// <summary>High-confidence bot detection - good training data</summary>
    HighConfidenceDetection,

    /// <summary>Minimal detection from fast-path abort (UA-only classification)</summary>
    MinimalDetection,

    /// <summary>Request for full 8-layer analysis on a fast-path sample</summary>
    FullAnalysisRequest,

    /// <summary>Full 8-layer detection completed (includes all detector results)</summary>
    FullDetection,

    // ==========================================
    // Pattern & Learning Events
    // ==========================================

    /// <summary>Pattern discovered by LLM/ONNX</summary>
    PatternDiscovered,

    /// <summary>Inconsistency detected between signals</summary>
    InconsistencyDetected,

    /// <summary>User feedback (confirmed bot / false positive)</summary>
    UserFeedback,

    /// <summary>Client-side browser fingerprinting validation results</summary>
    ClientSideValidation,

    /// <summary>Request for model inference (async)</summary>
    InferenceRequest,

    /// <summary>Model update available (reserved for future use)</summary>
    ModelUpdated,

    // ==========================================
    // Drift & Feedback Events
    // ==========================================

    /// <summary>
    ///     Fast-path drift detected - UA pattern no longer matches full analysis.
    ///     Contains: uaPattern, disagreementRate, totalSamples, recommendedAction
    /// </summary>
    FastPathDriftDetected,

    /// <summary>
    ///     New bot signature discovered that should be fed back to fast path.
    ///     Contains: signature type (UA, IP, characteristic), pattern, confidence
    /// </summary>
    SignatureFeedback,

    /// <summary>
    ///     Request to update fast-path rules based on learned patterns (reserved for future use).
    /// </summary>
    FastPathRuleUpdate,

    // ==========================================
    // Intent / Threat Classification Events
    // ==========================================

    /// <summary>
    ///     LLM intent classification completed for a session.
    ///     Contains: threat score, category, reasoning, intent features.
    ///     Consumed by IntentLearningHandler to embed into intent HNSW index.
    /// </summary>
    IntentClassified
}

/// <summary>
///     Payload carried on the <see cref="Mostlylucid.Ephemeral.TypedSignalSink{TPayload}"/>
///     that fronts the learning fabric. Raised by the hot path (drift, LLM
///     classification, intent), sensed by out-of-band learning handlers
///     (drift analysis, reputation maintenance, HNSW index writers, anomaly
///     saver).
/// </summary>
public class LearningEvent
{
    public required LearningEventType Type { get; init; }
    public required string Source { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Feature vector for ML training</summary>
    public Dictionary<string, double>? Features { get; init; }

    /// <summary>Label for supervised learning (true = bot)</summary>
    public bool? Label { get; init; }

    /// <summary>Confidence score from detection</summary>
    public double? Confidence { get; init; }

    /// <summary>Pattern string (for pattern learning)</summary>
    public string? Pattern { get; init; }

    /// <summary>Request ID for correlation</summary>
    public string? RequestId { get; init; }

    /// <summary>Additional metadata</summary>
    public Dictionary<string, object>? Metadata { get; init; }
}