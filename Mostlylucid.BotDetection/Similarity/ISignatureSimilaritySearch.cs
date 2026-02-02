namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Interface for signature similarity search using approximate nearest neighbor algorithms.
///     Allows finding behaviorally similar request signatures to leverage past detection results.
/// </summary>
public interface ISignatureSimilaritySearch
{
    /// <summary>
    ///     Find the most similar signatures to the given feature vector.
    /// </summary>
    /// <param name="vector">Feature vector to search for</param>
    /// <param name="topK">Maximum number of results to return</param>
    /// <param name="minSimilarity">Minimum cosine similarity threshold (0.0 to 1.0)</param>
    /// <returns>List of similar signatures ordered by distance (closest first)</returns>
    Task<IReadOnlyList<SimilarSignature>> FindSimilarAsync(float[] vector, int topK = 5, float minSimilarity = 0.80f);

    /// <summary>
    ///     Add a new signature vector to the index.
    /// </summary>
    /// <param name="vector">Feature vector</param>
    /// <param name="signatureId">Unique identifier for this signature</param>
    /// <param name="wasBot">Whether this signature was classified as a bot</param>
    /// <param name="confidence">Detection confidence (0.0 to 1.0)</param>
    Task AddAsync(float[] vector, string signatureId, bool wasBot, double confidence);

    /// <summary>
    ///     Persist the current index to disk.
    /// </summary>
    Task SaveAsync();

    /// <summary>
    ///     Load the index from disk.
    /// </summary>
    Task LoadAsync();

    /// <summary>
    ///     Number of vectors currently in the index.
    /// </summary>
    int Count { get; }
}

/// <summary>
///     Result of a similarity search.
/// </summary>
/// <param name="SignatureId">Unique identifier of the matched signature</param>
/// <param name="Distance">Cosine distance (lower = more similar; similarity = 1 - distance)</param>
/// <param name="WasBot">Whether the matched signature was classified as a bot</param>
/// <param name="Confidence">Detection confidence of the matched signature</param>
public record SimilarSignature(string SignatureId, float Distance, bool WasBot, double Confidence);
