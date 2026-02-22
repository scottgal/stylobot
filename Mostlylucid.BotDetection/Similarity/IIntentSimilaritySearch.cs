namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Interface for intent-based similarity search using approximate nearest neighbor algorithms.
///     Separate from ISignatureSimilaritySearch — this indexes WHAT sessions do (intent),
///     not WHO they are (identity). Enables the learning loop: LLM classifies novel patterns,
///     embeddings get stored, future similar sessions match without LLM.
/// </summary>
public interface IIntentSimilaritySearch
{
    /// <summary>
    ///     Find the most similar intent patterns to the given intent vector.
    /// </summary>
    /// <param name="vector">Intent feature vector (32 dimensions)</param>
    /// <param name="topK">Maximum number of results to return</param>
    /// <param name="minSimilarity">Minimum cosine similarity threshold (0.0 to 1.0)</param>
    /// <returns>List of similar intents ordered by distance (closest first)</returns>
    Task<IReadOnlyList<SimilarIntent>> FindSimilarAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.75f);

    /// <summary>
    ///     Add a new intent vector to the index.
    /// </summary>
    /// <param name="vector">Intent feature vector (32 dimensions)</param>
    /// <param name="signatureId">Signature that produced this intent pattern</param>
    /// <param name="threatScore">Threat score assigned (0.0 to 1.0)</param>
    /// <param name="intentCategory">Intent category (browsing, scanning, attacking, etc.)</param>
    /// <param name="reasoning">Optional LLM reasoning for this classification</param>
    Task AddAsync(float[] vector, string signatureId,
        double threatScore, string intentCategory, string? reasoning = null);

    /// <summary>
    ///     Persist the current index to disk.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    ///     Load the index from disk.
    /// </summary>
    Task LoadAsync();

    /// <summary>
    ///     Number of intent vectors currently in the index.
    /// </summary>
    int Count { get; }
}

/// <summary>
///     Result of an intent similarity search.
/// </summary>
/// <param name="SignatureId">Signature that produced the matched intent pattern</param>
/// <param name="Distance">Cosine distance (lower = more similar; similarity = 1 - distance)</param>
/// <param name="ThreatScore">Threat score of the matched pattern (0.0 to 1.0)</param>
/// <param name="IntentCategory">Intent category of the matched pattern</param>
public record SimilarIntent(
    string SignatureId, float Distance,
    double ThreatScore, string IntentCategory);
