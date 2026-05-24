using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Grouping;

namespace Mostlylucid.BotDetection.Test.Grouping;

/// <summary>
///     Exercises the grouper's tier 3 (session-vector cosine) with a stub
///     <see cref="ISessionSimilarityLookup"/>. Real similarity index is
///     covered separately in <c>BoundedSessionSimilarityLookupTests</c>.
/// </summary>
public class GrouperVectorTierTests
{
    private static (BehaviouralGrouper grouper, StubLookup lookup) Build(GroupingOptions? options = null)
    {
        options ??= new GroupingOptions();
        var lookup = new StubLookup();
        var grouper = new BehaviouralGrouper(
            new TestMonitor<GroupingOptions>(options),
            NullLogger<BehaviouralGrouper>.Instance,
            new SubnetRotationTracker(),
            lookup);
        return (grouper, lookup);
    }

    private static GroupingInput Bot(string signature) =>
        new() { Signature = signature, BotProbability = 0.92, RiskBand = "High", IsBot = true };

    [Fact]
    public void NeighbourAboveThreshold_CollapsesViaVectorTier()
    {
        var (grouper, lookup) = Build();
        lookup.SetNeighbour("sig:a", new SimilarityNeighbour("sig:b", 0.95f));

        var k = grouper.Resolve(Bot("sig:a"));

        Assert.Equal(GroupKeySource.SessionVectorCosine, k.Source);
        // Canonical value should be the alphabetically smaller signature
        // so both members of the pair resolve to the same key.
        Assert.Equal("sig:a", k.Value);
    }

    [Fact]
    public void NeighbourBelowThreshold_FallsThroughToLowerTiers()
    {
        var (grouper, lookup) = Build();
        lookup.SetNeighbour("sig:a", new SimilarityNeighbour("sig:b", 0.80f));   // below 0.92 default

        var k = grouper.Resolve(Bot("sig:a"));

        Assert.NotEqual(GroupKeySource.SessionVectorCosine, k.Source);
    }

    [Fact]
    public void NoNeighbourCached_FallsThrough_ColdRead()
    {
        // Lookup returns null = cold cache. Tier 3 doesn't fire on the
        // first call; subsequent calls within the TTL will pick up the
        // background populate. The grouper falls through cleanly.
        var (grouper, _) = Build();
        var k = grouper.Resolve(Bot("sig:a"));

        Assert.NotEqual(GroupKeySource.SessionVectorCosine, k.Source);
        Assert.Equal(GroupKeySource.RawSignature, k.Source);
    }

    [Fact]
    public void CanonicalKey_StableAcrossBothSidesOfPair()
    {
        // If A's neighbour is B and B's neighbour is A, both resolve to
        // the same group key (alphabetically smaller wins).
        var (grouper, lookup) = Build();
        lookup.SetNeighbour("sig:a", new SimilarityNeighbour("sig:b", 0.95f));
        lookup.SetNeighbour("sig:b", new SimilarityNeighbour("sig:a", 0.95f));

        var kA = grouper.Resolve(Bot("sig:a"));
        var kB = grouper.Resolve(Bot("sig:b"));

        Assert.Equal(kA.Canonical, kB.Canonical);
    }

    [Fact]
    public void VectorTierLoses_ToIdentityAndCluster()
    {
        var (grouper, lookup) = Build();
        lookup.SetNeighbour("sig:a", new SimilarityNeighbour("sig:b", 0.95f));

        var withIdentity = Bot("sig:a") with { FingerprintId = "fp_x" };
        Assert.Equal(GroupKeySource.Identity, grouper.Resolve(withIdentity).Source);

        var withCluster = Bot("sig:a") with { ClusterId = "c47" };
        Assert.Equal(GroupKeySource.Cluster, grouper.Resolve(withCluster).Source);
    }

    [Fact]
    public void TierDisabled_FallsThrough()
    {
        var (grouper, lookup) = Build(new GroupingOptions { EnableVectorTier = false });
        lookup.SetNeighbour("sig:a", new SimilarityNeighbour("sig:b", 0.95f));

        var k = grouper.Resolve(Bot("sig:a"));

        Assert.NotEqual(GroupKeySource.SessionVectorCosine, k.Source);
    }

    [Fact]
    public void HumanWithCachedNeighbour_StillReturnsHuman()
    {
        // The bot-only gate runs FIRST. Even if a human signature has a
        // populated neighbour, they're never grouped.
        var (grouper, lookup) = Build();
        lookup.SetNeighbour("sig:human", new SimilarityNeighbour("sig:other", 0.99f));

        var human = new GroupingInput
        {
            Signature = "sig:human",
            BotProbability = 0.05,
            RiskBand = "VeryLow",
            IsBot = false
        };
        var k = grouper.Resolve(human);

        Assert.Equal(GroupKeySource.Human, k.Source);
    }

    private sealed class StubLookup : ISessionSimilarityLookup
    {
        private readonly Dictionary<string, SimilarityNeighbour> _map = new(StringComparer.Ordinal);
        public void SetNeighbour(string sig, SimilarityNeighbour n) => _map[sig] = n;
        public SimilarityNeighbour? FindNearestBotNeighbour(string signature, float minCosine)
        {
            if (_map.TryGetValue(signature, out var n) && n.Cosine >= minCosine) return n;
            return null;
        }
    }

    private sealed class TestMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;
        public T Get(string? name) => CurrentValue;
        public IDisposable OnChange(Action<T, string?> listener) => new NoopDisposable();
        private sealed class NoopDisposable : IDisposable { public void Dispose() { } }
    }
}
