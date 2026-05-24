using Mostlylucid.BotDetection.Clustering.Knn;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Clustering.Knn;

public class BruteForceKnnGraphBuilderTests
{
    private static BotClusterService.FeatureVector Feature(string sig, double rate) =>
        new() { Signature = sig, RequestRate = rate };

    /// <summary>Linear-distance similarity: easier to reason about for the graph topology tests.</summary>
    private static double DistanceSimilarity(
        BotClusterService.FeatureVector a, BotClusterService.FeatureVector b)
    {
        var diff = Math.Abs(a.RequestRate - b.RequestRate);
        return Math.Max(0, 1.0 - diff);
    }

    [Fact]
    public void Build_AboveThreshold_SymmetricAdjacency()
    {
        var builder = new BruteForceKnnGraphBuilder();
        var features = new List<BotClusterService.FeatureVector>
        {
            Feature("a", 1.0),
            Feature("b", 1.1),    // diff 0.1 -> sim 0.9
            Feature("c", 5.0)     // far away -> sim 0 to both
        };

        var graph = builder.Build(features, DistanceSimilarity, k: 5, minSimilarity: 0.5);

        // Symmetric edges a <-> b with sim ~0.9 (float tolerance).
        Assert.Single(graph[0]);
        Assert.Single(graph[1]);
        Assert.Equal(1, graph[0][0].Neighbor);
        Assert.Equal(0, graph[1][0].Neighbor);
        Assert.Equal(0.9, graph[0][0].Weight, precision: 5);
        Assert.Equal(0.9, graph[1][0].Weight, precision: 5);
        Assert.Empty(graph[2]);                // c isolated
    }

    [Fact]
    public void Build_BelowThreshold_NoEdge()
    {
        var builder = new BruteForceKnnGraphBuilder();
        var features = new List<BotClusterService.FeatureVector>
        {
            Feature("a", 0.0),
            Feature("b", 0.6)     // diff 0.6 -> sim 0.4 (below 0.5)
        };

        var graph = builder.Build(features, DistanceSimilarity, k: 5, minSimilarity: 0.5);

        Assert.Empty(graph[0]);
        Assert.Empty(graph[1]);
    }

    [Fact]
    public void Build_KCap_DropsWorstNeighbour()
    {
        // Node 0 at rate 1.0 has neighbours at 1.1 (sim 0.9), 1.2 (0.8),
        // 1.3 (0.7), 1.4 (0.6). With K=2, keep the two best (1.1, 1.2).
        var builder = new BruteForceKnnGraphBuilder();
        var features = new List<BotClusterService.FeatureVector>
        {
            Feature("a", 1.0),
            Feature("b", 1.1),
            Feature("c", 1.2),
            Feature("d", 1.3),
            Feature("e", 1.4)
        };

        var graph = builder.Build(features, DistanceSimilarity, k: 2, minSimilarity: 0.5);

        var neighboursOfA = graph[0].Select(e => e.Neighbor).OrderBy(n => n).ToList();
        Assert.Equal(2, neighboursOfA.Count);
        Assert.Contains(1, neighboursOfA);  // b is the closest
        Assert.Contains(2, neighboursOfA);  // c next
    }

    [Fact]
    public void Build_EmptyFeatures_ReturnsEmptyAdjacency()
    {
        var builder = new BruteForceKnnGraphBuilder();
        var graph = builder.Build(
            new List<BotClusterService.FeatureVector>(),
            DistanceSimilarity, k: 5, minSimilarity: 0.5);

        Assert.Empty(graph);
    }

    [Fact]
    public void Build_SingleNode_NoEdges()
    {
        var builder = new BruteForceKnnGraphBuilder();
        var graph = builder.Build(
            new List<BotClusterService.FeatureVector> { Feature("only", 1.0) },
            DistanceSimilarity, k: 5, minSimilarity: 0.5);

        Assert.Empty(graph[0]);
    }

    [Fact]
    public void Build_AllPairsAboveThreshold_DenseGraph()
    {
        var builder = new BruteForceKnnGraphBuilder();
        var features = new List<BotClusterService.FeatureVector>
        {
            Feature("a", 1.0),
            Feature("b", 1.05),
            Feature("c", 1.1)
        };

        var graph = builder.Build(features, DistanceSimilarity, k: 10, minSimilarity: 0.5);

        Assert.Equal(2, graph[0].Count);
        Assert.Equal(2, graph[1].Count);
        Assert.Equal(2, graph[2].Count);
    }
}
