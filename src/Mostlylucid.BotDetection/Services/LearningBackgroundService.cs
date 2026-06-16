using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Processes learning events. Listens for triggers and runs inference /
///     learning asynchronously on a per-tick drain pass.
///     <para>
///         <b>Wave 2 architectural-drift remediation (Category B).</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> on the
///         learning bus reader. Now subscribes to <see cref="TickCadence.Tick1s"/>
///         via <see cref="IScheduleCoordinator"/>; each tick drains whatever
///         landed since the last tick via
///         <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/> and
///         dispatches each event sequentially to the relevant handlers. The
///         1-second cadence matches the legacy "process as fast as events
///         arrive" semantics within a one-tick latency budget; learning is
///         high-throughput off the hot path so <see cref="CostHint.High"/>
///         signals the per-tick concurrency cap.
///     </para>
///     <para>
///         The <see cref="ILearningEventBus"/> is wired separately (the
///         project registers <see cref="BoundedChannelLearningBus"/> as the
///         default implementation); this class only reads from
///         <see cref="ILearningEventBus.Reader"/>.
///     </para>
/// </summary>
public sealed class LearningBackgroundService : IDisposable
{
    private readonly ILearningEventBus _eventBus;
    private readonly IEnumerable<ILearningEventHandler> _handlers;
    private readonly ILicenseState _licenseState;
    private readonly ILogger<LearningBackgroundService> _logger;
    private readonly BotDetectionOptions _options;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public LearningBackgroundService(
        ILearningEventBus eventBus,
        ILogger<LearningBackgroundService> logger,
        IOptions<BotDetectionOptions> options,
        IEnumerable<ILearningEventHandler> handlers,
        ILicenseState licenseState,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _eventBus = eventBus;
        _logger = logger;
        _options = options.Value;
        _handlers = handlers;
        _licenseState = licenseState;

        // Optional so existing direct-construction tests that exercise
        // ProcessEventAsync in isolation keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1s,
                "LearningBackgroundService",
                CostHint.High,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: drain whatever
    ///     learning events landed since the last tick via
    ///     <see cref="System.Threading.Channels.ChannelReader{T}.TryRead"/>
    ///     and dispatch each through the registered handlers. Learning is
    ///     off the hot path so a 1-second latency budget per event is
    ///     acceptable; the ScheduleCoordinator's single-flight guarantee
    ///     prevents re-entry while a tick is still draining.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        while (_eventBus.Reader.TryRead(out var evt))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ProcessEventAsync(evt, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing learning event: {Type}", evt.Type);
            }
        }
    }

    private async Task ProcessEventAsync(LearningEvent evt, CancellationToken ct)
    {
        if (_licenseState.LearningFrozen)
        {
            _logger.LogDebug("Learning frozen, skipping event: {Type}", evt.Type);
            return;
        }

        _logger.LogDebug("Processing learning event: {Type} from {Source}", evt.Type, evt.Source);

        // Find handlers interested in this event type
        var relevantHandlers = _handlers
            .Where(h => h.HandledEventTypes.Contains(evt.Type))
            .ToList();

        foreach (var handler in relevantHandlers) await handler.HandleAsync(evt, ct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
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
    private readonly ILearningEventBus _eventBus;
    private readonly object _lock = new();
    private readonly ILogger<PatternAccumulatorHandler> _logger;
    private readonly Dictionary<string, int> _patternCounts = new();

    public PatternAccumulatorHandler(
        ILogger<PatternAccumulatorHandler> logger,
        ILearningEventBus eventBus)
    {
        _logger = logger;
        _eventBus = eventBus;
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

        // Trigger analysis when we've seen the pattern enough times
        if (count == InferenceTriggers.PatternAnalysisCount)
        {
            _logger.LogInformation(
                "Pattern '{Pattern}' hit threshold ({Count}), triggering analysis",
                evt.Pattern, count);

            _eventBus.TryPublish(new LearningEvent
            {
                Type = LearningEventType.InferenceRequest,
                Source = nameof(PatternAccumulatorHandler),
                Pattern = evt.Pattern,
                Metadata = new Dictionary<string, object>
                {
                    ["occurrences"] = count,
                    ["trigger"] = "pattern_threshold"
                }
            });
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