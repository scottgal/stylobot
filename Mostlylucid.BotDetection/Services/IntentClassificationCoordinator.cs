using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that processes LLM intent classification requests sequentially.
///     Uses a bounded Channel with DropOldest backpressure.
///     On result: (1) updates reputation cache, (2) vectorizes + adds to intent HNSW,
///     (3) publishes IntentClassified learning event for the learning loop.
///     When no LlmClassificationService is registered (no LLM provider), uses heuristic fallback.
/// </summary>
public class IntentClassificationCoordinator : BackgroundService
{
    private const int DefaultChannelCapacity = 100;

    private readonly Channel<IntentClassificationRequest> _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly IIntentSimilaritySearch _intentSearch;
    private readonly IntentVectorizer _vectorizer;
    private readonly IPatternReputationCache _reputationCache;
    private readonly ILearningEventBus? _learningBus;
    private readonly ILogger<IntentClassificationCoordinator> _logger;

    private long _totalProcessed;

    public IntentClassificationCoordinator(
        ILogger<IntentClassificationCoordinator> logger,
        IServiceProvider serviceProvider,
        IIntentSimilaritySearch intentSearch,
        IntentVectorizer vectorizer,
        IPatternReputationCache reputationCache,
        ILearningEventBus? learningBus = null)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _intentSearch = intentSearch;
        _vectorizer = vectorizer;
        _reputationCache = reputationCache;
        _learningBus = learningBus;

        _channel = Channel.CreateBounded<IntentClassificationRequest>(
            new BoundedChannelOptions(DefaultChannelCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });
    }

    /// <summary>Current number of items waiting in the queue.</summary>
    public int QueueDepth => _channel.Reader.Count;

    /// <summary>Total requests processed since startup.</summary>
    public long TotalProcessed => Interlocked.Read(ref _totalProcessed);

    /// <summary>
    ///     Try to enqueue a session snapshot for background LLM intent classification.
    /// </summary>
    public bool TryEnqueue(IntentClassificationRequest request)
    {
        if (!_channel.Writer.TryWrite(request))
        {
            _logger.LogDebug("Intent classification channel full, dropping request {RequestId}", request.RequestId);
            return false;
        }

        _logger.LogDebug(
            "Enqueued intent classification for {RequestId} sig={Signature} heuristic={Threat:F2} (depth={Depth})",
            request.RequestId,
            request.PrimarySignature[..Math.Min(8, request.PrimarySignature.Length)],
            request.HeuristicThreatScore,
            _channel.Reader.Count);
        return true;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "IntentClassificationCoordinator started (capacity={Capacity}, sequential processing)",
            DefaultChannelCapacity);

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
                    _logger.LogWarning(ex, "Intent classification failed for {RequestId}", request.RequestId);
                }
                finally
                {
                    Interlocked.Increment(ref _totalProcessed);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Expected on shutdown
        }

        _logger.LogInformation("IntentClassificationCoordinator stopped. Total processed: {Count}", TotalProcessed);
    }

    private async Task ProcessRequestAsync(IntentClassificationRequest request, CancellationToken ct)
    {
        double threatScore;
        string category;
        string? reasoning;

        // Try LLM classification if available
        var llmService = _serviceProvider.GetService(typeof(ILlmClassificationService));
        if (llmService is ILlmClassificationService llm)
        {
            var prompt = IntentPromptBuilder.BuildPrompt(request.SessionSummary);
            try
            {
                var response = await llm.ClassifyAsync(prompt, ct);
                var parsed = ParseLlmResponse(response);
                if (parsed.HasValue)
                {
                    threatScore = parsed.Value.Threat;
                    category = parsed.Value.Category;
                    reasoning = parsed.Value.Reasoning;

                    _logger.LogDebug(
                        "LLM intent classification for {Sig}: threat={Threat:F2}, category={Cat}",
                        request.PrimarySignature[..Math.Min(8, request.PrimarySignature.Length)],
                        threatScore, category);
                }
                else
                {
                    // LLM returned unparseable response — use heuristic
                    threatScore = request.HeuristicThreatScore;
                    category = "unknown";
                    reasoning = "LLM response could not be parsed";
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "LLM intent classification failed, using heuristic");
                threatScore = request.HeuristicThreatScore;
                category = "unknown";
                reasoning = "LLM unavailable, used heuristic";
            }
        }
        else
        {
            // No LLM provider — use heuristic score directly
            threatScore = request.HeuristicThreatScore;
            category = InferCategoryFromSignals(request.Signals);
            reasoning = "No LLM provider, used heuristic";
        }

        // Learning loop: embed into intent HNSW index
        await _intentSearch.AddAsync(
            request.IntentVector,
            request.PrimarySignature,
            threatScore,
            category,
            reasoning);

        // Publish learning event for other handlers
        _learningBus?.TryPublish(new LearningEvent
        {
            Type = LearningEventType.IntentClassified,
            Source = "IntentClassificationCoordinator",
            Confidence = threatScore,
            RequestId = request.RequestId,
            Metadata = new Dictionary<string, object>
            {
                ["signature"] = request.PrimarySignature,
                ["threat_score"] = threatScore,
                ["category"] = category,
                ["reasoning"] = reasoning ?? "",
                ["llm_classified"] = llmService != null
            }
        });

        _logger.LogInformation(
            "Intent classified: sig={Sig} threat={Threat:F2} category={Cat} llm={Llm}",
            request.PrimarySignature[..Math.Min(8, request.PrimarySignature.Length)],
            threatScore, category, llmService != null);
    }

    private static (double Threat, string Category, string Reasoning)? ParseLlmResponse(string response)
    {
        try
        {
            // Try to parse JSON from the LLM response
            var trimmed = response.Trim();

            // Extract JSON if wrapped in markdown code block
            if (trimmed.StartsWith("```"))
            {
                var startIdx = trimmed.IndexOf('{');
                var endIdx = trimmed.LastIndexOf('}');
                if (startIdx >= 0 && endIdx > startIdx)
                    trimmed = trimmed[startIdx..(endIdx + 1)];
            }

            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;

            var threat = root.TryGetProperty("threat", out var t) ? t.GetDouble() : 0.0;
            var cat = root.TryGetProperty("category", out var c) ? c.GetString() ?? "unknown" : "unknown";
            var reason = root.TryGetProperty("reasoning", out var r) ? r.GetString() ?? "" : "";

            return (Math.Clamp(threat, 0.0, 1.0), cat, reason);
        }
        catch
        {
            return null;
        }
    }

    private static string InferCategoryFromSignals(IReadOnlyDictionary<string, object> signals)
    {
        if (signals.TryGetValue(SignalKeys.AttackDetected, out var atk) && atk is true)
        {
            if (signals.TryGetValue(SignalKeys.AttackSqli, out var sqli) && sqli is true)
                return "attacking";
            if (signals.TryGetValue(SignalKeys.AttackXss, out var xss) && xss is true)
                return "attacking";
            if (signals.TryGetValue(SignalKeys.AttackPathProbe, out var probe) && probe is true)
                return "scanning";
            if (signals.TryGetValue(SignalKeys.AttackConfigExposure, out var cfg) && cfg is true)
                return "scanning";
            return "reconnaissance";
        }

        if (signals.TryGetValue(SignalKeys.ResponseScanPatternDetected, out var scan) && scan is true)
            return "scanning";

        if (signals.TryGetValue(SignalKeys.ResponseHoneypotHits, out var hp) && hp is int hpVal && hpVal > 0)
            return "attacking";

        return "browsing";
    }
}

/// <summary>
///     Interface for LLM classification services.
///     Implemented by LLM provider packages (Ollama, LlamaSharp, etc.)
/// </summary>
public interface ILlmClassificationService
{
    Task<string> ClassifyAsync(string prompt, CancellationToken ct = default);
}
