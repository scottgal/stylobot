namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Brute-force scan over all fingerprint centroids and active observation vectors. No native
///     dependency, no virtual table; pure C# cosine over the rows the store enumerates. Acceptable
///     up to a few thousand active fingerprints; beyond that, switch to the sqlite-vec implementation.
/// </summary>
public sealed class BruteForceIdentityAnchorIndex : IIdentityAnchorIndex
{
    private readonly IFingerprintStore _store;

    public BruteForceIdentityAnchorIndex(IFingerprintStore store) => _store = store;

    public async Task<IReadOnlyList<FingerprintCandidate>> SearchAsync(
        float[] vector,
        int topK,
        CancellationToken ct = default)
    {
        // Two top-K heaps: one over centroid scores, one over observation scores per fingerprint.
        var centroidScores = new SortedSet<(double Score, string Id)>(CandidateComparer.Instance);
        var observationScores = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var fp in await _store.ListFingerprintsAsync(ct))
        {
            var s = Cosine(vector, fp.Centroid);
            AddTopK(centroidScores, topK, (s, fp.FingerprintId));
        }

        foreach (var (id, vec) in await _store.ListActiveObservationsAsync(ct))
        {
            var s = Cosine(vector, vec);
            if (!observationScores.TryGetValue(id, out var existing) || s > existing)
                observationScores[id] = s;
        }

        // Union: every fingerprint that appears in either set, with its (centroid, best-obs) scores.
        var union = new Dictionary<string, FingerprintCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var (score, id) in centroidScores)
        {
            observationScores.TryGetValue(id, out var obs);
            union[id] = new FingerprintCandidate(id, score, obs);
        }
        foreach (var (id, obs) in observationScores)
        {
            if (union.ContainsKey(id)) continue;
            union[id] = new FingerprintCandidate(id, CentroidScore: 0, BestObsScore: obs);
        }

        // Keep top-K by max(centroid, obs).
        return union.Values
            .OrderByDescending(c => Math.Max(c.CentroidScore, c.BestObsScore))
            .Take(topK)
            .ToList();
    }

    private static void AddTopK(SortedSet<(double Score, string Id)> heap, int topK, (double Score, string Id) entry)
    {
        heap.Add(entry);
        if (heap.Count > topK) heap.Remove(heap.Min);
    }

    internal static double Cosine(float[] a, float[] b)
    {
        // Inputs are L2-normalised at composition time, so cosine collapses to a dot product.
        if (a.Length != b.Length) return 0;
        double dot = 0;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot;
    }

    /// <summary>
    ///     Weighted cosine. Inputs are L2-normalised but weights break that invariant, so this
    ///     re-normalises by the weighted norms.
    /// </summary>
    public static double WeightedCosine(float[] a, float[] b, float[] weights)
    {
        if (a.Length != b.Length || a.Length != weights.Length) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var w = weights[i];
            dot += w * a[i] * b[i];
            normA += w * a[i] * a[i];
            normB += w * b[i] * b[i];
        }
        var denom = Math.Sqrt(normA * normB);
        return denom > 0 ? dot / denom : 0;
    }

    private sealed class CandidateComparer : IComparer<(double Score, string Id)>
    {
        public static readonly CandidateComparer Instance = new();
        public int Compare((double Score, string Id) x, (double Score, string Id) y)
        {
            var s = x.Score.CompareTo(y.Score);
            return s != 0 ? s : string.CompareOrdinal(x.Id, y.Id);
        }
    }
}
