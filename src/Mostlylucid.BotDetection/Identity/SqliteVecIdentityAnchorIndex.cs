using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Vector-search index that prefers the sqlite-vec (vec0) virtual tables when the
///     extension was loaded at store init time, and falls through to the brute-force
///     scan when it wasn't. The selection happens per call so a deployment can add the
///     extension without restart and the perf path engages on the next store re-init.
///
///     vec0 returns L2 distance ascending; vectors are L2-normalised at composition
///     time, so cosine similarity collapses to <c>1 - distance² / 2</c>. Centroid +
///     observation queries run in parallel on separate connections.
/// </summary>
public sealed class SqliteVecIdentityAnchorIndex : IIdentityAnchorIndex
{
    private readonly ILogger<SqliteVecIdentityAnchorIndex> _logger;
    private readonly SqliteFingerprintStore _store;
    private readonly BruteForceIdentityAnchorIndex _bruteFallback;

    public SqliteVecIdentityAnchorIndex(
        ILogger<SqliteVecIdentityAnchorIndex> logger,
        SqliteFingerprintStore store,
        BruteForceIdentityAnchorIndex bruteFallback)
    {
        _logger = logger;
        _store = store;
        _bruteFallback = bruteFallback;
    }

    public async Task<IReadOnlyList<FingerprintCandidate>> SearchAsync(
        float[] vector, int topK, CancellationToken ct = default)
    {
        await _store.EnsureInitialisedAsync(ct);
        if (!_store.IsVecAvailable)
            return await _bruteFallback.SearchAsync(vector, topK, ct);

        try
        {
            var centroidTask = _store.SearchVecCentroidsAsync(vector, topK, ct);
            var observationTask = _store.SearchVecObservationsAsync(vector, topK, ct);
            await Task.WhenAll(centroidTask, observationTask);

            var centroidScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, dist) in centroidTask.Result)
                centroidScores[id] = DistanceToCosine(dist);

            var observationScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var (id, dist) in observationTask.Result)
            {
                var score = DistanceToCosine(dist);
                if (!observationScores.TryGetValue(id, out var existing) || score > existing)
                    observationScores[id] = score;
            }

            return UnionTopK(centroidScores, observationScores, topK);
        }
        catch (Exception ex)
        {
            // Falls back so the matcher never sees an empty result because of vec0 trouble.
            _logger.LogWarning(ex, "vec0 KNN failed; falling back to brute force for this request");
            return await _bruteFallback.SearchAsync(vector, topK, ct);
        }
    }

    /// <summary>
    ///     Unions centroid + observation hits per fingerprint, retaining the better
    ///     score per source. Same shape used by <see cref="BruteForceIdentityAnchorIndex"/>
    ///     so vec0 and brute-force return byte-identical candidate sets given the same hits.
    /// </summary>
    internal static IReadOnlyList<FingerprintCandidate> UnionTopK(
        IReadOnlyDictionary<string, double> centroidScores,
        IReadOnlyDictionary<string, double> observationScores,
        int topK)
    {
        var union = new Dictionary<string, FingerprintCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, score) in centroidScores)
        {
            observationScores.TryGetValue(id, out var obs);
            union[id] = new FingerprintCandidate(id, score, obs);
        }
        foreach (var (id, obs) in observationScores)
        {
            if (union.ContainsKey(id)) continue;
            union[id] = new FingerprintCandidate(id, CentroidScore: 0, BestObsScore: obs);
        }
        return union.Values
            .OrderByDescending(c => Math.Max(c.CentroidScore, c.BestObsScore))
            .Take(topK)
            .ToList();
    }

    private static double DistanceToCosine(double distance)
        => Math.Clamp(1 - distance * distance / 2.0, -1.0, 1.0);
}
