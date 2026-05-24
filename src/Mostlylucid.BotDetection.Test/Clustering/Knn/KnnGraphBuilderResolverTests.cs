using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Clustering.Knn;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Clustering.Knn;

public class KnnGraphBuilderResolverTests
{
    private static BotClusterService.FeatureVector Feature(string sig) =>
        new() { Signature = sig, RequestRate = 1.0 };

    private static double TrivialSim(BotClusterService.FeatureVector a, BotClusterService.FeatureVector b) => 1.0;

    [Fact]
    public void Resolver_FallsBackToBruteForce_WhenVecBuilderThrows()
    {
        var brute = new BruteForceKnnGraphBuilder();
        // Pass a deliberately-broken "vec" builder that always throws.
        var vec = new ThrowingBuilder();
        var resolver = new KnnGraphBuilderResolver(
            // The resolver type signature requires the concrete sqlite-vec
            // builder, so use a real one but feed it a too-small feature set
            // -- it'll throw InvalidOperationException (below the min-size
            // gate) and the resolver will fall through.
            BuildRealSqliteVecBuilder(),
            brute,
            NullLogger<KnnGraphBuilderResolver>.Instance);

        var features = new List<BotClusterService.FeatureVector> { Feature("a"), Feature("b") };

        var graph = resolver.Build(features, TrivialSim, k: 5, minSimilarity: 0.5);

        Assert.NotNull(graph);
        Assert.Equal(2, graph.Count);
    }

    [Fact]
    public void Resolver_OnSubsequentCalls_SkipsVecOnceItFailed()
    {
        var vec = BuildRealSqliteVecBuilder();
        var brute = new CountingBuilder();
        var resolver = new KnnGraphBuilderResolver(
            vec, brute, NullLogger<KnnGraphBuilderResolver>.Instance);

        var features = new List<BotClusterService.FeatureVector> { Feature("a") };

        // First call: vec throws (below threshold), resolver falls back, counter = 1.
        resolver.Build(features, TrivialSim, 5, 0.5);
        Assert.Equal(1, brute.CallCount);

        // Subsequent call: skips vec entirely (sticky failure), goes direct
        // to brute force. Counter = 2.
        resolver.Build(features, TrivialSim, 5, 0.5);
        Assert.Equal(2, brute.CallCount);
    }

    private static SqliteVecKnnGraphBuilder BuildRealSqliteVecBuilder() =>
        new(NullLogger<SqliteVecKnnGraphBuilder>.Instance);

    private sealed class ThrowingBuilder : IKnnGraphBuilder
    {
        public Dictionary<int, List<(int Neighbor, double Weight)>> Build(
            IReadOnlyList<BotClusterService.FeatureVector> features,
            Func<BotClusterService.FeatureVector, BotClusterService.FeatureVector, double> similarity,
            int k, double minSimilarity, CancellationToken ct = default)
            => throw new InvalidOperationException("nope");
    }

    private sealed class CountingBuilder : IKnnGraphBuilder
    {
        public int CallCount;
        private readonly BruteForceKnnGraphBuilder _real = new();

        public Dictionary<int, List<(int Neighbor, double Weight)>> Build(
            IReadOnlyList<BotClusterService.FeatureVector> features,
            Func<BotClusterService.FeatureVector, BotClusterService.FeatureVector, double> similarity,
            int k, double minSimilarity, CancellationToken ct = default)
        {
            CallCount++;
            return _real.Build(features, similarity, k, minSimilarity, ct);
        }
    }
}
