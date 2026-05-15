using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Vector-search index that prefers the sqlite-vec (vec0) virtual tables when the
///     extension was loaded at store init time, and falls through to the brute-force
///     scan when it wasn't. The selection happens per call rather than at construction
///     so a deployment can add the extension without restart and the perf path engages
///     on the next store re-init.
///
///     Score parity with the brute-force engine: vec0 returns L2 distance ascending; the
///     vectors are L2-normalised at composition time, so cosine similarity collapses to
///     <c>1 - distance² / 2</c>. We translate every vec0 hit into the same cosine
///     score the brute-force engine returns, then union centroid + observation hits per
///     fingerprint with the better score retained per source — exactly what the matcher
///     and the brute-force fallback do.
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
            return await SearchVecAsync(vector, topK, ct);
        }
        catch (Exception ex)
        {
            // vec0 KNN failed mid-flight (corrupt index, shape mismatch on a stale row,
            // version skew). Fall back to brute force so the request still resolves —
            // the matcher only sees a slower path, never an empty result.
            _logger.LogWarning(ex, "vec0 KNN failed; falling back to brute force for this request");
            return await _bruteFallback.SearchAsync(vector, topK, ct);
        }
    }

    private async Task<IReadOnlyList<FingerprintCandidate>> SearchVecAsync(
        float[] vector, int topK, CancellationToken ct)
    {
        await using var conn = await _store.OpenVecConnectionAsync(ct);
        var blob = SqliteFingerprintStore.FloatsToBlob(vector);

        // Centroid KNN: top-K nearest fingerprint centroids.
        var centroidScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fingerprint_id, distance
                  FROM fingerprints_vec
                 WHERE centroid MATCH @vec AND k = @k
                """;
            cmd.Parameters.AddWithValue("@vec", blob);
            cmd.Parameters.AddWithValue("@k", topK);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var dist = reader.GetDouble(1);
                centroidScores[id] = DistanceToCosine(dist);
            }
        }

        // Observation KNN: top-K nearest unabsorbed observations. Each observation row
        // carries its parent fingerprint_id as a stored aux column, so we get the join
        // for free in the query result.
        var observationScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT fingerprint_id, distance
                  FROM observations_vec
                 WHERE vector MATCH @vec AND k = @k
                """;
            cmd.Parameters.AddWithValue("@vec", blob);
            cmd.Parameters.AddWithValue("@k", topK);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var id = reader.GetString(0);
                var dist = reader.GetDouble(1);
                var score = DistanceToCosine(dist);
                if (!observationScores.TryGetValue(id, out var existing) || score > existing)
                    observationScores[id] = score;
            }
        }

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

    /// <summary>
    ///     Convert vec0's L2 distance to the cosine similarity the rest of the matcher
    ///     consumes. Vectors are L2-normalised at composition time, so the identity
    ///     <c>cosine = 1 - distance² / 2</c> holds. Clamped to [-1, 1] for numeric safety.
    /// </summary>
    private static double DistanceToCosine(double distance)
        => Math.Clamp(1 - distance * distance / 2.0, -1.0, 1.0);
}
