using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Processes LLM intent classification requests sequentially out of a
///     bounded Channel&lt;T&gt; with DropOldest backpressure. On result:
///     (1) updates reputation cache, (2) vectorizes + adds to intent HNSW,
///     (3) publishes IntentClassified learning event for the learning loop.
///     When no LlmClassificationService is registered (no LLM provider), uses
///     heuristic fallback.
///     <para>
///         <b>Wave 2 architectural-drift remediation (Category B).</b> Mirror
///         of <see cref="LlmClassificationCoordinator"/>: was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c>; now
///         subscribes to <see cref="TickCadence.Tick10s"/> via
///         <see cref="IScheduleCoordinator"/> and each tick drains the channel
///         via <see cref="ChannelReader{T}.TryRead"/>, processing each request
///         sequentially within the tick.
///     </para>
/// </summary>
public class IntentClassificationCoordinator : IDisposable
{
    private const int DefaultChannelCapacity = 100;

    private readonly Channel<IntentClassificationRequest> _channel;
    private readonly IServiceProvider _serviceProvider;
    private readonly IIntentSimilaritySearch _intentSearch;
    private readonly IntentVectorizer _vectorizer;
    private readonly IPatternReputationCache _reputationCache;
    private readonly ILearningEventBus? _learningBus;
    private readonly ILogger<IntentClassificationCoordinator> _logger;
    private readonly IDisposable? _subscription;
    private int _disposed;

    private long _totalProcessed;

    public IntentClassificationCoordinator(
        ILogger<IntentClassificationCoordinator> logger,
        IServiceProvider serviceProvider,
        IIntentSimilaritySearch intentSearch,
        IntentVectorizer vectorizer,
        IPatternReputationCache reputationCache,
        ILearningEventBus? learningBus = null,
        IScheduleCoordinator? scheduleCoordinator = null)
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

        // Optional so existing direct-construction tests that exercise
        // TryEnqueue / ProcessRequestAsync in isolation keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "IntentClassificationCoordinator",
                CostHint.High,
                OnTickAsync);
        }
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

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: drain any intent
    ///     classification requests landed since the last tick via
    ///     <see cref="ChannelReader{T}.TryRead"/> and process each sequentially.
    ///     LLM dispatch is slow; ScheduleCoordinator's single-flight guarantee
    ///     prevents re-entry while a tick is still draining.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        while (_channel.Reader.TryRead(out var request))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                await ProcessRequestAsync(request, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
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

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }

    private async Task ProcessRequestAsync(IntentClassificationRequest request, CancellationToken ct)
    {
        double threatScore;
        string category;
        string? reasoning;

        // Try LLM classification if available
        var llmService = _serviceProvider.GetService(typeof(ILlmClassificationService));
        var llmClassified = false;
        if (llmService is ILlmClassificationService llm)
        {
            var prompt = IntentPromptBuilder.BuildPrompt(request.SessionSummary);
            try
            {
                var response = await llm.ClassifyAsync(prompt, ct);
                var parsed = ParseLlmResponse(response);
                if (parsed.HasValue)
                {
                    llmClassified = true;
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
                    // LLM returned unparseable response - use heuristic
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
            // No LLM provider - use heuristic score directly
            threatScore = request.HeuristicThreatScore;
            category = InferCategoryFromSignals(request.Signals);
            reasoning = "No LLM provider, used heuristic";
        }

        var learningEvent = new LearningEvent
        {
            Type = LearningEventType.IntentClassified,
            Source = "IntentClassificationCoordinator",
            Confidence = threatScore,
            RequestId = request.RequestId,
            Features = request.IntentFeatures.ToDictionary(
                kv => kv.Key,
                kv => (double)kv.Value,
                StringComparer.OrdinalIgnoreCase),
            Metadata = new Dictionary<string, object>
            {
                ["signature"] = request.PrimarySignature,
                ["threat_score"] = threatScore,
                ["category"] = category,
                ["reasoning"] = reasoning ?? "",
                ["llm_classified"] = llmClassified
            }
        };

        var published = _learningBus?.TryPublish(learningEvent) == true;
        if (!published)
        {
            await _intentSearch.AddAsync(
                request.IntentVector,
                request.PrimarySignature,
                threatScore,
                category,
                reasoning);
        }

        _logger.LogInformation(
            "Intent classified: sig={Sig} threat={Threat:F2} category={Cat} llm={Llm}",
            request.PrimarySignature[..Math.Min(8, request.PrimarySignature.Length)],
            threatScore, category, llmClassified);
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
