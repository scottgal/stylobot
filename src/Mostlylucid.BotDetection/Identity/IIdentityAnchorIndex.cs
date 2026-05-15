namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Vector-search index over fingerprint centroids and recent observation vectors. Two
///     implementations exist: <see cref="BruteForceIdentityAnchorIndex"/> (no native dep,
///     in-DB cosine UDF) and a sqlite-vec backed implementation (vec0 virtual tables).
///
///     Query semantics are identical between implementations; only SQL/storage shape differs.
///     The C# layer chooses at startup based on whether sqlite-vec loaded successfully.
/// </summary>
public interface IIdentityAnchorIndex
{
    /// <summary>
    ///     Find candidate fingerprints whose centroid OR a recent unabsorbed observation is
    ///     close to <paramref name="vector"/> by plain (un-weighted) cosine. Returns at most
    ///     <paramref name="topK"/> distinct candidates per source (centroid index, observation
    ///     index), unioned and deduped by fingerprint_id with the better score retained per
    ///     source. The matcher then re-ranks with each candidate's stored weights.
    /// </summary>
    Task<IReadOnlyList<FingerprintCandidate>> SearchAsync(
        float[] vector,
        int topK,
        CancellationToken ct = default);
}
