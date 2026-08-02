using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Learning;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Dispatches <see cref="LearningEvent"/>s raised on
///     <see cref="ILearningCoordinator.Signals"/> to the registered
///     <see cref="ILearningEventHandler"/>s. Replaces the retired
///     channel-bus drain loop: subscribes to
///     <see cref="TypedSignalSink{T}.TypedSignalRaised"/> at construction
///     and fires handlers per-event (fire-and-forget on the thread pool so
///     the raiser thread stays hot).
///     <para>
///         Handler failures are logged and swallowed; one bad handler
///         must not stop the others. Dispatch is per-event serial
///         (each event's handlers run sequentially), across-event
///         concurrent (independent events fan out).
///     </para>
/// </summary>
public sealed class LearningBackgroundService : IDisposable
{
    private readonly IEnumerable<ILearningEventHandler> _handlers;
    private readonly TypedSignalSink<LearningEvent> _signals;
    private readonly ILogger<LearningBackgroundService> _logger;
    private readonly BotDetectionOptions _options;
    private readonly Posture.IDetectionPostureProvider _postureProvider;
    private readonly Action<SignalEvent<LearningEvent>> _onRaised;
    private int _disposed;

    public LearningBackgroundService(
        TypedSignalSink<LearningEvent> signals,
        ILogger<LearningBackgroundService> logger,
        IOptions<BotDetectionOptions> options,
        IEnumerable<ILearningEventHandler> handlers,
        Posture.IDetectionPostureProvider? postureProvider = null)
    {
        _signals = signals;
        _logger = logger;
        _options = options.Value;
        _handlers = handlers;
        _postureProvider = postureProvider ?? Posture.FullDetectionPostureProvider.Instance;

        _onRaised = OnLearningRaised;
        _signals.TypedSignalRaised += _onRaised;

        // Catch-up on the raise that triggered our lazy boot. TypedSignalRaised
        // is a plain C# multicast event, so the invocation that caused the
        // init signal to fire (and thus caused DI to construct this
        // dispatcher) uses a subscriber snapshot taken before our
        // subscription landed. Sense() walks the sink's retention window
        // and dispatches anything already in-flight so we don't lose the
        // triggering event or any raises that arrived while DI was
        // resolving. Runs once per singleton lifetime; safe on tests that
        // construct directly (empty sink → empty replay).
        foreach (var evt in _signals.Sense())
        {
            _ = Task.Run(() => ProcessEventAsync(evt.Payload, CancellationToken.None));
        }
    }

    private void OnLearningRaised(SignalEvent<LearningEvent> evt)
    {
        if (_disposed != 0) return;

        // Fire-and-forget: keeps the producer thread hot; each event's
        // handlers still run sequentially inside its dispatch task.
        _ = Task.Run(() => ProcessEventAsync(evt.Payload, CancellationToken.None));
    }

    /// <summary>
    ///     Public for direct-drive tests that construct the service without
    ///     wiring through the coordinator's raise notification.
    /// </summary>
    public async Task ProcessEventAsync(LearningEvent evt, CancellationToken ct)
    {
        // Global host-posture gate (e.g. a license-expiry freeze): the single choke point
        // every learning-event producer funnels through, so gating here covers all of them
        // (current and future) without touching each producer individually.
        if (!_postureProvider.LearningEnabled) return;

        _logger.LogDebug("Processing learning event: {Type} from {Source}", evt.Type, evt.Source);

        foreach (var handler in _handlers)
        {
            if (!handler.HandledEventTypes.Contains(evt.Type)) continue;
            try
            {
                await handler.HandleAsync(evt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Handler {Handler} failed for {Type}",
                    handler.GetType().Name, evt.Type);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _signals.TypedSignalRaised -= _onRaised; }
        catch { /* sink already torn down */ }
    }
}

/// <summary>
///     Handler for learning events
/// </summary>
public interface ILearningEventHandler
{
    /// <summary>Event types this handler processes</summary>
    IReadOnlySet<LearningEventType> HandledEventTypes { get; }

    /// <summary>Process the event</summary>
    Task HandleAsync(LearningEvent evt, CancellationToken ct = default);
}

/// <summary>
///     Triggers that cause inference to run.
///     Uses detection confidence (certainty of verdict) not bot probability.
/// </summary>
public static class InferenceTriggers
{
    /// <summary>
    ///     Detection confidence threshold above which we store for training.
    ///     High confidence = system is sure about its verdict → good training data.
    /// </summary>
    public const double HighConfidenceThreshold = 0.85;

    /// <summary>
    ///     Minimum detection confidence for pattern extraction.
    ///     Must be reasonably sure of the verdict before extracting patterns.
    /// </summary>
    public const double PatternExtractionThreshold = 0.7;

    /// <summary>
    ///     Detection confidence below which we trigger full learning for uncertain detections.
    ///     Combined with high bot probability, this means "looks like a bot but we're not sure".
    /// </summary>
    public const double UncertainConfidenceThreshold = 0.6;

    /// <summary>
    ///     Bot probability threshold for triggering uncertain-detection learning.
    ///     When probability > this AND confidence &lt; UncertainConfidenceThreshold → learn.
    /// </summary>
    public const double UncertainProbabilityThreshold = 0.5;

    /// <summary>
    ///     Number of similar detections before triggering pattern analysis.
    /// </summary>
    public const int PatternAnalysisCount = 5;
}

/// <summary>
///     Handler that triggers ML inference for high-confidence detections
/// </summary>
public class InferenceHandler : ILearningEventHandler
{
    private readonly ILogger<InferenceHandler> _logger;
    private readonly BotDetectionOptions _options;

    public InferenceHandler(
        ILogger<InferenceHandler> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.HighConfidenceDetection,
        LearningEventType.InferenceRequest
    };

    public async Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        switch (evt.Type)
        {
            case LearningEventType.HighConfidenceDetection:
                await HandleHighConfidenceDetection(evt, ct);
                break;

            case LearningEventType.InferenceRequest:
                await RunInference(evt, ct);
                break;
        }
    }

    private async Task HandleHighConfidenceDetection(LearningEvent evt, CancellationToken ct)
    {
        if (evt.Confidence < InferenceTriggers.HighConfidenceThreshold)
            return;

        _logger.LogDebug(
            "High-confidence detection ({Confidence:F2}) - storing for training",
            evt.Confidence);

        // Store features for future training
        if (evt.Features != null) await StoreTrainingDataAsync(evt.Features, evt.Label ?? true, ct);

        // If confidence is very high, extract pattern for fast-path matching
        if (evt.Confidence >= 0.95 && !string.IsNullOrEmpty(evt.Pattern))
            await StoreLearnedPatternAsync(evt.Pattern, evt.Confidence.Value, ct);
    }

    private async Task RunInference(LearningEvent evt, CancellationToken ct)
    {
        if (evt.Features == null)
        {
            _logger.LogWarning("Inference request without features");
            return;
        }

        _logger.LogDebug("Running async inference for request {RequestId}", evt.RequestId);

        // This would call into ONNX or LLM for inference
        // Results could be published back via another event or stored
        await Task.CompletedTask; // Placeholder for actual inference
    }

    private Task StoreTrainingDataAsync(
        Dictionary<string, double> features,
        bool isBot,
        CancellationToken ct)
    {
        // Store in training data store (SQLite, file, etc.)
        _logger.LogDebug("Stored training sample: isBot={IsBot}, features={Count}",
            isBot, features.Count);
        return Task.CompletedTask;
    }

    private Task StoreLearnedPatternAsync(string pattern, double confidence, CancellationToken ct)
    {
        _logger.LogDebug("Stored learned pattern: {Pattern} (confidence={Confidence:F2})",
            pattern, confidence);
        return Task.CompletedTask;
    }
}

/// <summary>
///     Handler that accumulates patterns and triggers analysis when threshold reached
/// </summary>
public class PatternAccumulatorHandler : ILearningEventHandler
{
    private readonly Mostlylucid.Ephemeral.TypedSignalSink<LearningEvent> _signals;
    private readonly object _lock = new();
    private readonly ILogger<PatternAccumulatorHandler> _logger;
    private readonly Dictionary<string, int> _patternCounts = new();

    public PatternAccumulatorHandler(
        ILogger<PatternAccumulatorHandler> logger,
        Mostlylucid.Ephemeral.TypedSignalSink<LearningEvent> signals)
    {
        _logger = logger;
        _signals = signals;
    }

    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.PatternDiscovered,
        LearningEventType.InconsistencyDetected
    };

    public Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(evt.Pattern))
            return Task.CompletedTask;

        int count;
        lock (_lock)
        {
            _patternCounts.TryGetValue(evt.Pattern, out count);
            count++;
            _patternCounts[evt.Pattern] = count;
        }

        _logger.LogDebug("Pattern '{Pattern}' seen {Count} times", evt.Pattern, count);

        // Trigger analysis when we've seen the pattern enough times.
        // Re-emitting into the shared sink -- the coordinator that dispatched
        // us is a consumer of the same sink; no coordinator dependency needed.
        if (count == InferenceTriggers.PatternAnalysisCount)
        {
            _logger.LogInformation(
                "Pattern '{Pattern}' hit threshold ({Count}), triggering analysis",
                evt.Pattern, count);

            var inferenceEvent = new LearningEvent
            {
                Type = LearningEventType.InferenceRequest,
                Source = nameof(PatternAccumulatorHandler),
                Pattern = evt.Pattern,
                Metadata = new Dictionary<string, object>
                {
                    ["occurrences"] = count,
                    ["trigger"] = "pattern_threshold"
                }
            };
            var key = LearningSignalKeys.For(inferenceEvent.Type);
            _signals.Raise(key.Name, inferenceEvent, key: inferenceEvent.Pattern);
        }

        return Task.CompletedTask;
    }
}

/// <summary>
///     Handler for user feedback (confirmed bot / false positive)
/// </summary>
public class FeedbackHandler : ILearningEventHandler
{
    private readonly ILogger<FeedbackHandler> _logger;

    public FeedbackHandler(ILogger<FeedbackHandler> logger)
    {
        _logger = logger;
    }

    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.UserFeedback
    };

    public Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        var wasBot = evt.Label ?? false;
        var wasCorrect = evt.Metadata?.TryGetValue("detection_correct", out var correct) == true
                         && correct is bool b && b;

        _logger.LogInformation(
            "User feedback received: wasBot={WasBot}, detectionCorrect={Correct}, requestId={RequestId}",
            wasBot, wasCorrect, evt.RequestId);

        // Update model weights, pattern confidence, etc.
        // This is where active learning happens

        return Task.CompletedTask;
    }
}