using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that processes learning events.
///     Listens for triggers and runs inference/learning asynchronously.
/// </summary>
public class LearningBackgroundService : BackgroundService
{
    private readonly ILearningEventBus _eventBus;
    private readonly IEnumerable<ILearningEventHandler> _handlers;
    private readonly ILogger<LearningBackgroundService> _logger;
    private readonly BotDetectionOptions _options;

    public LearningBackgroundService(
        ILearningEventBus eventBus,
        ILogger<LearningBackgroundService> logger,
        IOptions<BotDetectionOptions> options,
        IEnumerable<ILearningEventHandler> handlers)
    {
        _eventBus = eventBus;
        _logger = logger;
        _options = options.Value;
        _handlers = handlers;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Learning background service started");

        try
        {
            await foreach (var evt in _eventBus.Reader.ReadAllAsync(stoppingToken))
                try
                {
                    await ProcessEventAsync(evt, stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error processing learning event: {Type}", evt.Type);
                }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        _logger.LogInformation("Learning background service stopped");
    }

    private async Task ProcessEventAsync(LearningEvent evt, CancellationToken ct)
    {
        _logger.LogDebug("Processing learning event: {Type} from {Source}", evt.Type, evt.Source);

        // Find handlers interested in this event type
        var relevantHandlers = _handlers
            .Where(h => h.HandledEventTypes.Contains(evt.Type))
            .ToList();

        foreach (var handler in relevantHandlers) await handler.HandleAsync(evt, ct);
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