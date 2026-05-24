using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Clustering.Knn;

/// <summary>
///     Builds the sparse similarity graph that <see cref="LeidenClustering"/>
///     consumes. Today's brute-force implementation does <c>O(N²)</c> pair
///     similarity; a sqlite-vec backed implementation does <c>O(N log N)</c>
///     via vec0 K-nearest-neighbour queries on the behavioural centroids.
/// </summary>
/// <remarks>
///     <para>
///         The builder receives the feature-vector list + a similarity
///         function (so the caller controls the blend of heuristic +
///         cosine + spectral terms) + a similarity threshold and a K cap.
///         Output is a symmetric adjacency dictionary that
///         <see cref="LeidenClustering"/> consumes verbatim.
///     </para>
///     <para>
///         The K cap is the key correctness contract: brute-force
///         compatibility means "keep up to K best neighbours per node",
///         not "consider every pair". Setting K to <see cref="int.MaxValue"/>
///         gives byte-equivalent behaviour to today's all-pairs build
///         when the brute-force implementation is selected.
///     </para>
///     <para>
///         The audit at <c>docs/research/2026-05-24-grouping-systems-audit.md</c>
///         lays out the rationale and the speed/quality tradeoffs.
///     </para>
/// </remarks>
public interface IKnnGraphBuilder
{
    /// <summary>
    ///     Build the sparse adjacency graph for Leiden.
    /// </summary>
    /// <param name="features">Feature vectors, one per signature (positionally indexed).</param>
    /// <param name="similarity">
    ///     Blended-similarity function used to weight each candidate edge.
    ///     Called only for candidate pairs surfaced by the underlying
    ///     KNN search; not all <c>O(N²)</c> pairs.
    /// </param>
    /// <param name="k">Maximum neighbours retained per node. Lower K → sparser graph → tighter Leiden clusters.</param>
    /// <param name="minSimilarity">Edges below this weight are dropped after the similarity blend.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    ///     Symmetric adjacency: node-index -&gt; list of (neighbour-index, weight).
    ///     A given (i, j) edge appears in both <c>adj[i]</c> and <c>adj[j]</c>
    ///     with the same weight. Every node has an entry even if it has
    ///     no edges (empty list).
    /// </returns>
    Dictionary<int, List<(int Neighbor, double Weight)>> Build(
        IReadOnlyList<BotClusterService.FeatureVector> features,
        Func<BotClusterService.FeatureVector, BotClusterService.FeatureVector, double> similarity,
        int k,
        double minSimilarity,
        CancellationToken ct = default);
}
