using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Clustering.Knn;

/// <summary>
///     Pure C# fallback when sqlite-vec isn't available. Scans every
///     <c>(i, j)</c> pair once, computes the full blended similarity,
///     keeps the top K neighbours per node above the threshold. Same
///     algorithm as the historic inline <c>BuildSimilarityGraph</c>
///     with one improvement -- explicit K cap so the produced graph
///     matches what the sqlite-vec backed builder would produce on
///     the same inputs.
/// </summary>
/// <remarks>
///     Complexity stays <c>O(N²)</c> but only one pass over the pair
///     space; the K-cap reduces the resulting Leiden input by a factor
///     of <c>K / N</c>, which tightens the clusters relative to the
///     pre-refactor full-density graph.
/// </remarks>
public sealed class BruteForceKnnGraphBuilder : IKnnGraphBuilder
{
    public Dictionary<int, List<(int Neighbor, double Weight)>> Build(
        IReadOnlyList<BotClusterService.FeatureVector> features,
        Func<BotClusterService.FeatureVector, BotClusterService.FeatureVector, double> similarity,
        int k,
        double minSimilarity,
        CancellationToken ct = default)
    {
        var n = features.Count;
        // Top-K min-heap per node (max-heap of (weight, neighbor), so we
        // can evict the worst candidate when we exceed K).
        var heaps = new List<(int Neighbor, double Weight)>[n];
        for (var i = 0; i < n; i++) heaps[i] = new List<(int, double)>(Math.Min(k, n - 1) + 1);

        for (var i = 0; i < n; i++)
        {
            ct.ThrowIfCancellationRequested();
            for (var j = i + 1; j < n; j++)
            {
                var sim = similarity(features[i], features[j]);
                if (sim < minSimilarity) continue;

                AddCapped(heaps[i], j, sim, k);
                AddCapped(heaps[j], i, sim, k);
            }
        }

        var adjacency = new Dictionary<int, List<(int Neighbor, double Weight)>>(n);
        for (var i = 0; i < n; i++) adjacency[i] = heaps[i];

        return adjacency;
    }

    /// <summary>
    ///     Maintain top-K neighbours by weight. Linear scan to find the
    ///     weakest -- adequate at typical K=10-30. A real min-heap would
    ///     scale better at K&gt;100 but we don't need it.
    /// </summary>
    private static void AddCapped(List<(int Neighbor, double Weight)> list, int neighbor, double weight, int k)
    {
        if (list.Count < k)
        {
            list.Add((neighbor, weight));
            return;
        }

        var weakestIdx = -1;
        var weakestWeight = double.MaxValue;
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i].Weight < weakestWeight)
            {
                weakestWeight = list[i].Weight;
                weakestIdx = i;
            }
        }

        if (weight > weakestWeight && weakestIdx >= 0)
            list[weakestIdx] = (neighbor, weight);
    }
}
