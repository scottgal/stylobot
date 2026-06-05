using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

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
        return DotProductSimd(a, b);
    }

    /// <summary>
    ///     Weighted cosine. Inputs are L2-normalised but weights break that invariant, so this
    ///     re-normalises by the weighted norms.
    ///
    ///     SIMD path: on ARM64 NEON (Vector&lt;float&gt;.Count = 4) or x64 AVX2 (8) the inner
    ///     loop processes Count elements per iteration. For the 88-dim layout v2 this means
    ///     22 NEON iterations vs 88 scalar iterations. Tail handles the remainder when
    ///     vector length isn't a multiple of Vector&lt;float&gt;.Count.
    /// </summary>
    public static double WeightedCosine(float[] a, float[] b, float[] weights)
    {
        if (a.Length != b.Length || a.Length != weights.Length) return 0;

        var (dot, normA, normB) = WeightedCosineAccumulatorsSimd(a, b, weights);
        var denom = Math.Sqrt(normA * normB);
        return denom > 0 ? dot / denom : 0;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static double DotProductSimd(float[] a, float[] b)
    {
        var len = a.Length;
        var aSpan = MemoryMarshal.Cast<float, float>(a);
        var bSpan = MemoryMarshal.Cast<float, float>(b);

        var width = Vector<float>.Count;
        var i = 0;
        var dot = Vector<float>.Zero;

        for (; i <= len - width; i += width)
        {
            var va = new Vector<float>(aSpan.Slice(i, width));
            var vb = new Vector<float>(bSpan.Slice(i, width));
            dot += va * vb;
        }

        double sum = Vector.Sum(dot);
        for (; i < len; i++) sum += a[i] * b[i];
        return sum;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static (double Dot, double NormA, double NormB) WeightedCosineAccumulatorsSimd(
        float[] a, float[] b, float[] weights)
    {
        var len = a.Length;
        var aSpan = MemoryMarshal.Cast<float, float>(a);
        var bSpan = MemoryMarshal.Cast<float, float>(b);
        var wSpan = MemoryMarshal.Cast<float, float>(weights);

        var width = Vector<float>.Count;
        var dot = Vector<float>.Zero;
        var normA = Vector<float>.Zero;
        var normB = Vector<float>.Zero;
        var i = 0;

        for (; i <= len - width; i += width)
        {
            var va = new Vector<float>(aSpan.Slice(i, width));
            var vb = new Vector<float>(bSpan.Slice(i, width));
            var vw = new Vector<float>(wSpan.Slice(i, width));
            var wa = vw * va;
            var wb = vw * vb;
            dot += wa * vb;
            normA += wa * va;
            normB += wb * vb;
        }

        double sumDot = Vector.Sum(dot);
        double sumA = Vector.Sum(normA);
        double sumB = Vector.Sum(normB);
        for (; i < len; i++)
        {
            var w = weights[i];
            sumDot += w * a[i] * b[i];
            sumA += w * a[i] * a[i];
            sumB += w * b[i] * b[i];
        }

        return (sumDot, sumA, sumB);
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
