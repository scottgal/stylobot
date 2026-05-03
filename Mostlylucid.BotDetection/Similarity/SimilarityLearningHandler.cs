using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Learning event handler that feeds high-confidence detection results
///     into the HNSW similarity search index.
///     On HighConfidenceDetection events, extracts features, vectorizes them,
///     and adds the vector to the HNSW index for future similarity lookups.
/// </summary>
public sealed class SimilarityLearningHandler : ILearningEventHandler
{
    private readonly FeatureVectorizer _vectorizer;
    private readonly ISignatureSimilaritySearch _search;
    private readonly ILogger<SimilarityLearningHandler> _logger;

    public SimilarityLearningHandler(
        FeatureVectorizer vectorizer,
        ISignatureSimilaritySearch search,
        ILogger<SimilarityLearningHandler> logger)
    {
        _vectorizer = vectorizer;
        _search = search;
        _logger = logger;
    }

    public IReadOnlySet<LearningEventType> HandledEventTypes => new HashSet<LearningEventType>
    {
        LearningEventType.HighConfidenceDetection,
        LearningEventType.FullDetection
    };

    public async Task HandleAsync(LearningEvent evt, CancellationToken ct = default)
    {
        if (evt.Features == null || evt.Features.Count == 0)
            return;

        try
        {
            // Convert the double-typed features from the learning event to float for vectorization
            var floatFeatures = new Dictionary<string, float>(evt.Features.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in evt.Features)
                floatFeatures[key] = (float)value;

            var vector = _vectorizer.Vectorize(floatFeatures);

            // Determine if this was a bot detection
            var wasBot = evt.Label ?? (evt.Confidence.HasValue && evt.Confidence.Value > 0.7);
            var confidence = evt.Confidence ?? 0.5;

            // Prefer the stable visitor signature over the per-request ID so learning
            // survives repeated visits and does not fill the index with one-off IDs.
            var signatureId = ExtractStableSignature(evt) ?? evt.RequestId ?? Guid.NewGuid().ToString("N")[..12];

            // Build embedding context from metadata for semantic similarity.
            // Uses User-Agent + request path - these are the signals that best distinguish
            // bot identities across the heuristic feature dimensions.
            string? embeddingContext = null;
            if (evt.Metadata != null)
            {
                var parts = new List<string>(3);
                if (evt.Metadata.TryGetValue("userAgent", out var ua) && ua is string uaStr)
                    parts.Add($"UA:{uaStr}");
                if (evt.Metadata.TryGetValue("path", out var path) && path is string pathStr)
                    parts.Add($"Path:{pathStr}");
                parts.Add(wasBot ? "Bot" : "Human");
                embeddingContext = string.Join(" | ", parts);
            }

            await _search.AddAsync(vector, signatureId, wasBot, confidence, embeddingContext);

            _logger.LogDebug(
                "Added vector to similarity index: signature={SignatureId}, wasBot={WasBot}, confidence={Confidence:F2}, indexSize={Count}",
                signatureId, wasBot, confidence, _search.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to add vector to similarity index");
        }
    }

    private static string? ExtractStableSignature(LearningEvent evt)
    {
        if (evt.Metadata == null) return null;

        if (evt.Metadata.TryGetValue("primarySignature", out var primary) &&
            !string.IsNullOrWhiteSpace(primary?.ToString()))
            return primary.ToString();

        if (evt.Metadata.TryGetValue("signature", out var signature) &&
            !string.IsNullOrWhiteSpace(signature?.ToString()))
            return signature.ToString();

        return null;
    }
}
