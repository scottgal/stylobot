using System.Threading.Channels;

namespace Mostlylucid.BotDetection.Events;

/// <summary>
///     Inter-request event bus for learning and analytics.
///     Runs as a background service, processes events asynchronously.
///     Unlike BotSignalBus (intra-request), this is long-lived and cross-request.
/// </summary>
public interface ILearningEventBus
{
    /// <summary>
    ///     Get the event reader for background processing
    /// </summary>
    ChannelReader<LearningEvent> Reader { get; }

    /// <summary>
    ///     Publish a learning event (non-blocking, fire-and-forget)
    /// </summary>
    bool TryPublish(LearningEvent evt);

    /// <summary>
    ///     Complete the bus (for shutdown)
    /// </summary>
    void Complete();
}

/// <summary>
///     Types of learning events
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

    /// <summary>Model update available</summary>
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
    ///     Request to update fast-path rules based on learned patterns.
    ///     Consumed by the rule update service.
    /// </summary>
    FastPathRuleUpdate
}

/// <summary>
///     Event for the inter-request learning bus
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

/// <summary>
///     Channel-based implementation of the learning event bus.
///     Long-lived, shared across all requests.
/// </summary>
public sealed class LearningEventBus : ILearningEventBus, IDisposable
{
    private readonly Channel<LearningEvent> _channel;
    private bool _disposed;

    public LearningEventBus(int capacity = 10_000)
    {
        // Bounded channel to prevent memory issues if processing falls behind
        _channel = Channel.CreateBounded<LearningEvent>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest, // Drop oldest if full
            SingleReader = true, // Single background processor
            SingleWriter = false, // Multiple requests can publish
            AllowSynchronousContinuations = false // Don't block request threads
        });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Complete();
    }

    public bool TryPublish(LearningEvent evt)
    {
        if (_disposed) return false;
        return _channel.Writer.TryWrite(evt);
    }

    public ChannelReader<LearningEvent> Reader => _channel.Reader;

    public void Complete()
    {
        _channel.Writer.TryComplete();
    }
}