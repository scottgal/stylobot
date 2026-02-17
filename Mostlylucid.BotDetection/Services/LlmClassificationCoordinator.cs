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
    private readonly IBotNameSynthesizer? _nameSynthesizer;
    private readonly ILearningEventBus? _learningBus;
    private readonly ILogger<LlmClassificationCoordinator> _logger;
    private readonly BotDetectionOptions _options;
    private readonly IPatternReputationCache _reputationCache;
    private readonly PatternReputationUpdater _updater;
    private readonly ILlmResultCallback? _resultCallback;

    private long _totalProcessed;

    public LlmClassificationCoordinator(
        ILogger<LlmClassificationCoordinator> logger,
        LlmDetector detector,
        IPatternReputationCache reputationCache,
        PatternReputationUpdater updater,
        IOptions<BotDetectionOptions> options,
        ILlmResultCallback? resultCallback = null,
        ILearningEventBus? learningBus = null,
        IBotNameSynthesizer? nameSynthesizer = null)
    {
        _logger = logger;
        _detector = detector;
        _reputationCache = reputationCache;
        _updater = updater;
        _options = options.Value;
        _resultCallback = resultCallback;
        _learningBus = learningBus;
        _nameSynthesizer = nameSynthesizer;

        _channel = Channel.CreateBounded<LlmClassificationRequest>(
            new BoundedChannelOptions(_options.LlmCoordinator.ChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>Current number of items waiting in the queue.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>Queue utilization as a fraction of capacity (0.0 to 1.0+).</summary>
    public double QueueUtilization => (double)_channel.Reader.Count / _options.LlmCoordinator.ChannelCapacity;

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

        _logger.LogDebug("Enqueued LLM classification for {RequestId} sig={Signature} reason={Reason} (depth={Depth})",
            request.RequestId,
            request.PrimarySignature[..Math.Min(8, request.PrimarySignature.Length)],
            request.EnqueueReason ?? "unknown",
            _channel.Reader.Count);
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
            // Ollama returned nothing — try LlamaSharp name synthesizer as fallback
            if (_nameSynthesizer != null && _nameSynthesizer.IsReady)
            {
                await FallbackToNameSynthesizerAsync(request, ct);
            }
            else
            {
                _logger.LogDebug("LLM returned no classification for {RequestId} (no fallback available)", request.RequestId);
            }
            return;
        }

        var reason = result.Reasons.First();
        var description = reason.Detail;

        // Update ephemeral reputation cache with LLM result for ALL signature vectors.
        // This ensures churn-resistant identity: if IP changes but UA stays the same,
        // the UA vector still carries the LLM result forward.
        // Use ApplyEvidence to go through proper EWMA blending, state evaluation, and confidence tracking.
        // LLM evidence weight of 2.0 — authoritative but blended with existing evidence.
        var llmLabel = result.Confidence > 0.5 ? 1.0 : 0.0;
        const double llmEvidenceWeight = 2.0;

        if (request.SignatureVectors is { Count: > 0 })
        {
            foreach (var (vectorType, vectorHash) in request.SignatureVectors)
            {
                var patternId = $"{vectorType}:{vectorHash}";
                var existing = _reputationCache.Get(patternId);
                var updated = _updater.ApplyEvidence(existing, patternId, vectorType, vectorHash, llmLabel, llmEvidenceWeight);
                _reputationCache.Update(updated);
            }
        }
        else
        {
            // Fallback: single primary signature
            var patternId = $"ua:{request.PrimarySignature}";
            var existing = _reputationCache.Get(patternId);
            var updated = _updater.ApplyEvidence(existing, patternId, "UserAgent", request.PrimarySignature, llmLabel, llmEvidenceWeight);
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

    /// <summary>
    ///     Fallback: use IBotNameSynthesizer (LlamaSharp) when Ollama is not configured.
    ///     Generates a description from request signals and broadcasts via callback.
    /// </summary>
    private async Task FallbackToNameSynthesizerAsync(LlmClassificationRequest request, CancellationToken ct)
    {
        try
        {
            // Build minimal signals from the pre-built request info
            var signals = new Dictionary<string, object?>
            {
                ["detection.useragent.source"] = request.PreBuiltRequestInfo,
                ["request.signature"] = request.PrimarySignature,
                ["detection.heuristic.probability"] = request.HeuristicProbability
            };

            // Add signature vectors as signals
            if (request.SignatureVectors is { Count: > 0 })
            {
                foreach (var (vectorType, vectorHash) in request.SignatureVectors)
                    signals[$"signature.{vectorType}"] = vectorHash;
            }

            var (name, description) = await _nameSynthesizer!.SynthesizeDetailedAsync(signals, ct: ct);

            if (!string.IsNullOrWhiteSpace(description) && _resultCallback != null)
            {
                await _resultCallback.OnLlmResultAsync(
                    request.RequestId,
                    request.PrimarySignature,
                    description,
                    ct);

                _logger.LogDebug("LlamaSharp fallback generated description for {RequestId}: {Name}",
                    request.RequestId, name ?? "(unnamed)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LlamaSharp fallback failed for {RequestId}", request.RequestId);
        }
    }
}
