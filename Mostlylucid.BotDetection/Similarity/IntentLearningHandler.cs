using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Learning event handler that feeds intent classifications into the intent HNSW index.
///     Listens for IntentClassified (from LLM) and HighConfidenceDetection (when attack signals present).
///     This is the learning loop: LLM classifies → embed → HNSW stores → next similar session matches without LLM.
/// </summary>
public sealed class IntentLearningHandler : ILearningEventHandler
{
    private readonly IntentVectorizer _vectorizer;
    private readonly IIntentSimilaritySearch _intentSearch;
    private readonly ILogger<IntentLearningHandler> _logger;

    public IntentLearningHandler(
        IntentVectorizer vectorizer,
        IIntentSimilaritySearch intentSearch,
        ILogger<IntentLearningHandler> logger)
    {
        _vectorizer = vectorizer;
        _intentSearch = intentSearch;
        _logger = logger;
    }

    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.IntentClassified,
        LearningEventType.HighConfidenceDetection
    };

    public async Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        try
        {
            if (evt.Type == LearningEventType.IntentClassified)
            {
                await HandleIntentClassifiedAsync(evt);
            }
            else if (evt.Type == LearningEventType.HighConfidenceDetection)
            {
                await HandleHighConfidenceWithAttackAsync(evt);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to process intent learning event");
        }
    }

    private async Task HandleIntentClassifiedAsync(LearningEvent evt)
    {
        if (evt.Metadata == null) return;

        // IntentClassified events already have the intent vector from the coordinator
        // Extract metadata
        var signature = evt.Metadata.TryGetValue("signature", out var sig) ? sig?.ToString() ?? "" : "";
        var threatScore = evt.Confidence ?? 0.0;
        var category = evt.Metadata.TryGetValue("category", out var cat) ? cat?.ToString() ?? "unknown" : "unknown";
        var reasoning = evt.Metadata.TryGetValue("reasoning", out var r) ? r?.ToString() : null;

        if (string.IsNullOrEmpty(signature)) return;

        // Rebuild intent features from learning event features if available
        if (evt.Features is { Count: > 0 })
        {
            var floatFeatures = new Dictionary<string, float>(evt.Features.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in evt.Features)
                floatFeatures[key] = (float)value;

            var vector = _vectorizer.Vectorize(floatFeatures);
            await _intentSearch.AddAsync(vector, signature, threatScore, category, reasoning);

            _logger.LogDebug(
                "Intent learning: added LLM-classified vector for {Sig}, threat={Threat:F2}, category={Cat}, indexSize={Count}",
                signature[..Math.Min(8, signature.Length)], threatScore, category, _intentSearch.Count);
        }
    }

    private async Task HandleHighConfidenceWithAttackAsync(LearningEvent evt)
    {
        // Only process high-confidence detections that have attack signals
        if (evt.Metadata == null || evt.Features == null || evt.Features.Count == 0)
            return;

        // Check if attack signals are present
        var hasAttack = evt.Metadata.TryGetValue("attack_detected", out var atk) && atk is true;
        if (!hasAttack) return;

        var signature = evt.RequestId ?? Guid.NewGuid().ToString("N")[..12];
        var category = evt.Metadata.TryGetValue("attack_category", out var cat)
            ? cat?.ToString() ?? "attacking" : "attacking";

        // Build intent features from the detection features
        var floatFeatures = new Dictionary<string, float>(evt.Features.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in evt.Features)
            floatFeatures[key] = (float)value;

        var vector = _vectorizer.Vectorize(floatFeatures);
        var threatScore = evt.Confidence ?? 0.8;

        await _intentSearch.AddAsync(vector, signature, threatScore, category,
            "High-confidence detection with attack signals");

        _logger.LogDebug(
            "Intent learning: added high-confidence attack vector for {Sig}, threat={Threat:F2}, indexSize={Count}",
            signature[..Math.Min(8, signature.Length)], threatScore, _intentSearch.Count);
    }
}
