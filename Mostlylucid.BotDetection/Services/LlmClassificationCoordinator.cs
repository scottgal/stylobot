using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that processes LLM classification requests sequentially.
///     Uses a bounded Channel&lt;T&gt; with DropOldest backpressure.
///     Fire-and-forget from the detection pipeline — no parallelism, one at a time.
///     Tracks queue depth and provides adaptive sampling rates.
/// </summary>
public class LlmClassificationCoordinator : BackgroundService
{
    private readonly Channel<LlmClassificationRequest> _channel;
    private readonly LlmDetector _detector;
    private readonly ILearningEventBus? _learningBus;
    private readonly ILogger<LlmClassificationCoordinator> _logger;
    private readonly BotDetectionOptions _options;
    private readonly IPatternReputationCache _reputationCache;
    private readonly ILlmResultCallback? _resultCallback;

    private int _queueDepth;
    private long _totalProcessed;

    public LlmClassificationCoordinator(
        ILogger<LlmClassificationCoordinator> logger,
        LlmDetector detector,
        IPatternReputationCache reputationCache,
        IOptions<BotDetectionOptions> options,
        ILlmResultCallback? resultCallback = null,
        ILearningEventBus? learningBus = null)
    {
        _logger = logger;
        _detector = detector;
        _reputationCache = reputationCache;
        _options = options.Value;
        _resultCallback = resultCallback;
        _learningBus = learningBus;

        _channel = Channel.CreateBounded<LlmClassificationRequest>(
            new BoundedChannelOptions(_options.LlmCoordinator.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>Current number of items waiting in the queue.</summary>
    public int QueueDepth => _queueDepth;

    /// <summary>Queue utilization as a fraction of capacity (0.0 to 1.0+).</summary>
    public double QueueUtilization => (double)_queueDepth / _options.LlmCoordinator.ChannelCapacity;

    /// <summary>Total requests processed since startup.</summary>
    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);

    /// <summary>
    ///     Returns the current adaptive sample rate based on queue utilization.
    ///     When queue is empty → higher rate (keep Ollama busy).
    ///     When queue is filling → lower rate (avoid dropping).
    /// </summary>
    public double GetAdaptiveSampleRate()
    {
        var utilization = QueueUtilization;
        var baseRate = _options.LlmCoordinator.BaseSampleRate;

        return utilization switch
        {
            < 0.1 => baseRate * 3.0,   // Queue nearly empty — sample aggressively
            < 0.3 => baseRate * 2.0,   // Queue low — sample more
            < 0.6 => baseRate,         // Normal — base rate
            < 0.8 => baseRate * 0.5,   // Queue filling — reduce sampling
            _ => baseRate * 0.1        // Queue nearly full — minimal sampling
        };
    }

    /// <summary>
    ///     Try to enqueue a detection snapshot for background LLM classification.
    ///     Returns false if the channel is full (request dropped).
    /// </summary>
    public bool TryEnqueue(LlmClassificationRequest request)
    {
        if (!_channel.Writer.TryWrite(request))
        {
            _logger.LogDebug("LLM coordinator channel full, dropping request {RequestId}", request.RequestId);
            return false;
        }

        Interlocked.Increment(ref _queueDepth);
        _logger.LogDebug("Enqueued LLM classification for {RequestId} sig={Signature} reason={Reason} (depth={Depth})",
            request.RequestId,
            request.PrimarySignature[..Math.Min(8, request.PrimarySignature.Length)],
            request.EnqueueReason ?? "unknown",
            _queueDepth);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("LlmClassificationCoordinator started (capacity={Capacity}, sequential processing)",
            _options.LlmCoordinator.ChannelCapacity);

        try
        {
            await foreach (var request in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    await ProcessRequestAsync(request, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "LLM classification failed for {RequestId}", request.RequestId);
                }
                finally
                {
                    Interlocked.Decrement(ref _queueDepth);
                    Interlocked.Increment(ref _totalProcessed);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown — ReadAllAsync throws when token is cancelled
        }

        _logger.LogInformation("LlmClassificationCoordinator stopped");
    }

    private async Task ProcessRequestAsync(LlmClassificationRequest request, CancellationToken ct)
    {
        _logger.LogDebug("Processing LLM classification for {RequestId}", request.RequestId);

        var result = await _detector.DetectFromSnapshotAsync(request.PreBuiltRequestInfo, ct);

        if (result.Reasons.Count == 0)
        {
            _logger.LogDebug("LLM returned no classification for {RequestId}", request.RequestId);
            return;
        }

        var reason = result.Reasons.First();
        var description = reason.Detail;

        // Update ephemeral reputation cache with LLM result for ALL signature vectors.
        // This ensures churn-resistant identity: if IP changes but UA stays the same,
        // the UA vector still carries the LLM result forward.
        if (request.SignatureVectors is { Count: > 0 })
        {
            foreach (var (vectorType, vectorHash) in request.SignatureVectors)
            {
                var patternId = $"{vectorType}:{vectorHash}";
                var existing = _reputationCache.GetOrCreate(patternId, vectorType, vectorHash);
                var updated = existing with
                {
                    BotScore = result.Confidence,
                    Support = existing.Support + 1,
                    LastSeen = DateTimeOffset.UtcNow
                };
                _reputationCache.Update(updated);
            }
        }
        else
        {
            // Fallback: single primary signature
            var patternId = $"ua:{request.PrimarySignature}";
            var existing = _reputationCache.GetOrCreate(patternId, "UserAgent", request.PrimarySignature);
            var updated = existing with
            {
                BotScore = result.Confidence,
                Support = existing.Support + 1,
                LastSeen = DateTimeOffset.UtcNow
            };
            _reputationCache.Update(updated);
        }

        // Publish drift event if this was a drift/confirmation sample
        if ((request.IsDriftSample || request.IsConfirmationSample) && _learningBus != null)
        {
            var llmIsBot = result.Confidence > 0.5;
            var heuristicIsBot = request.HeuristicProbability > 0.5;
            var disagrees = llmIsBot != heuristicIsBot;

            _learningBus.TryPublish(new LearningEvent
            {
                Type = disagrees
                    ? LearningEventType.FastPathDriftDetected
                    : LearningEventType.HighConfidenceDetection,
                Source = "LlmCoordinator",
                Confidence = result.Confidence,
                Label = llmIsBot,
                Pattern = request.PrimarySignature,
                Metadata = new Dictionary<string, object>
                {
                    ["heuristicProb"] = request.HeuristicProbability,
                    ["llmProb"] = result.Confidence,
                    ["disagrees"] = disagrees,
                    ["isDriftSample"] = request.IsDriftSample,
                    ["isConfirmationSample"] = request.IsConfirmationSample
                }
            });

            if (disagrees)
            {
                _logger.LogInformation(
                    "Drift detected for {Signature}: heuristic={Heuristic:F2} vs LLM={Llm:F2}",
                    request.PrimarySignature[..Math.Min(8, request.PrimarySignature.Length)],
                    request.HeuristicProbability,
                    result.Confidence);
            }
        }

        // Broadcast result via callback (SignalR)
        if (_resultCallback != null && !string.IsNullOrWhiteSpace(description))
        {
            await _resultCallback.OnLlmResultAsync(
                request.RequestId,
                request.PrimarySignature,
                description,
                ct);
        }

        _logger.LogDebug("LLM classification complete for {RequestId}: isBot={IsBot}, confidence={Confidence:F2}",
            request.RequestId, reason.ConfidenceImpact > 0, result.Confidence);
    }
}
