using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Calibration covers two responsibilities — Fisher weight computation (pure math) and
///     archetype self-refinement (math + persistence). The pure-math tests pin down the
///     contract independent of any storage; the service test verifies the end-to-end loop
///     against a real SQLite store.
/// </summary>
public sealed class IdentityWeightCalibrationTests
{
    [Fact]
    public void ComputeFisherWeights_HighDiscriminatingDim_GetsHigherWeight()
    {
        // Two clusters, dim 0 separates them perfectly, dim 1 is pure noise within both.
        // Fisher should boost dim 0's weight above dim 1's.
        var members = new List<(string, float[])>
        {
            ("a", new float[] { 1.0f, 0.5f }),
            ("a", new float[] { 1.0f, 0.6f }),
            ("a", new float[] { 1.0f, 0.4f }),
            ("b", new float[] { 0.0f, 0.5f }),
            ("b", new float[] { 0.0f, 0.6f }),
            ("b", new float[] { 0.0f, 0.4f })
        };

        var weights = IdentityWeightMath.ComputeFisherWeights(members, dimension: 2,
            minFingerprints: 2, minWeight: 0.1, maxWeight: 10.0);

        Assert.NotNull(weights);
        Assert.True(weights![0] > weights[1],
            $"Discriminating dim 0 should weigh more than noise dim 1 — got {weights[0]:F3} vs {weights[1]:F3}");
    }

    [Fact]
    public void ComputeFisherWeights_OneCluster_ReturnsNull()
    {
        var members = new List<(string, float[])>
        {
            ("only", new float[] { 1.0f, 0.5f }),
            ("only", new float[] { 0.9f, 0.5f })
        };

        var weights = IdentityWeightMath.ComputeFisherWeights(members, dimension: 2,
            minFingerprints: 2, minWeight: 0.1, maxWeight: 10.0);

        Assert.Null(weights);
    }

    [Fact]
    public void ComputeFisherWeights_BelowMinFingerprints_ReturnsNull()
    {
        var members = new List<(string, float[])>
        {
            ("a", new float[] { 1.0f }),
            ("b", new float[] { 0.0f })
        };

        var weights = IdentityWeightMath.ComputeFisherWeights(members, dimension: 1,
            minFingerprints: 5, minWeight: 0.1, maxWeight: 10.0);

        Assert.Null(weights);
    }

    [Fact]
    public void ComputeFisherWeights_ResultMeansToOne()
    {
        // Per-fingerprint weights, global weights, and Fisher weights all share the same
        // normalisation contract: mean ≈ 1.0 so multiplicative composition doesn't inflate
        // weighted-cosine scores.
        var members = new List<(string, float[])>
        {
            ("a", new float[] { 1.0f, 0.5f, 0.3f }),
            ("a", new float[] { 0.9f, 0.4f, 0.2f }),
            ("b", new float[] { 0.0f, 0.5f, 0.7f }),
            ("b", new float[] { 0.1f, 0.4f, 0.8f })
        };

        var weights = IdentityWeightMath.ComputeFisherWeights(members, dimension: 3,
            minFingerprints: 2, minWeight: 0.1, maxWeight: 10.0);

        Assert.NotNull(weights);
        var mean = weights!.Sum() / weights.Length;
        // Mean lands close to 1.0; the post-normalise clamp at MinWeight can pull it slightly
        // above when the raw distribution has a long left tail (a few degenerate-low dims), so
        // the contract is "near 1.0" rather than "exactly 1.0".
        Assert.InRange(mean, 0.90, 1.10);
    }

    [Fact]
    public void RefineArchetypeCentroid_NoDescendants_ReturnsNull()
    {
        var archetype = new float[] { 1.0f, 0.0f, 0.5f };
        var refined = IdentityWeightMath.RefineArchetypeCentroid(archetype, Array.Empty<float[]>(), 0.7);
        Assert.Null(refined);
    }

    [Fact]
    public void RefineArchetypeCentroid_SingleDescendant_LightlyShifts()
    {
        // One descendant: α should be very small (1/11 ≈ 0.091), so refined stays close to
        // the seed archetype. The cap doesn't bind here.
        var archetype = new float[] { 1.0f, 0.0f };
        var descendant = new float[] { 0.0f, 1.0f };
        var refined = IdentityWeightMath.RefineArchetypeCentroid(
            archetype, new[] { descendant }, alphaCap: 0.7);

        Assert.NotNull(refined);
        Assert.InRange(refined![0], 0.85f, 0.95f);
        Assert.InRange(refined[1], 0.05f, 0.15f);
    }

    [Fact]
    public void RefineArchetypeCentroid_ManyDescendants_HitsCap()
    {
        // 100 descendants would give α = 100/110 ≈ 0.91, but cap = 0.4 binds.
        var archetype = new float[] { 1.0f };
        var descendants = Enumerable.Range(0, 100).Select(_ => new float[] { 0.0f }).ToList();
        var refined = IdentityWeightMath.RefineArchetypeCentroid(archetype, descendants, alphaCap: 0.4);

        Assert.NotNull(refined);
        // refined = (1 - 0.4) * 1.0 + 0.4 * 0.0 = 0.6
        Assert.InRange(refined![0], 0.55f, 0.65f);
    }
}

/// <summary>
///     End-to-end: feed the calibration service real fingerprints across two inferred client
///     types, run RunOnceAsync, and verify the global weights row landed and at least one
///     archetype was refined (when descendants exist).
/// </summary>
public sealed class IdentityWeightCalibrationServiceIntegrationTests : IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteFingerprintStore _store;
    private readonly IdentityArchetypeRegistry _registry;
    private readonly IdentityWeightCalibrationService _service;

    public IdentityWeightCalibrationServiceIntegrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-calib-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var encoder = new IdentityVectorEncoder(layout);
        _store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, layout);
        _registry = new IdentityArchetypeRegistry(NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
        _service = new IdentityWeightCalibrationService(
            NullLogger<IdentityWeightCalibrationService>.Instance, _store, _registry, options);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task RunOnceAsync_EmptyStore_ReportsNoComputation()
    {
        var result = await _service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, result.FingerprintCount);
        Assert.False(result.WeightsComputed);
        Assert.Equal(0, result.ArchetypesRefined);
    }

    [Fact]
    public async Task RunOnceAsync_TwoClusters_ComputesAndPersistsWeights()
    {
        // Pick two real archetype ids so the refinement path also fires.
        var archetypeIds = _registry.All.Take(2).Select(a => a.ArchetypeId).ToList();
        Assert.True(archetypeIds.Count >= 2, "Test needs at least two archetypes loaded");

        var dim = _store.Layout.Dimension;
        var v1 = MakeUnitVector(dim, seed: 1);
        var v2 = MakeUnitVector(dim, seed: 2);

        await _store.InsertFingerprintAsync(MakeFingerprint("fp-a-1", v1, archetypeIds[0]), "sig-a-1");
        await _store.InsertFingerprintAsync(MakeFingerprint("fp-a-2", v1, archetypeIds[0]), "sig-a-2");
        await _store.InsertFingerprintAsync(MakeFingerprint("fp-b-1", v2, archetypeIds[1]), "sig-b-1");
        await _store.InsertFingerprintAsync(MakeFingerprint("fp-b-2", v2, archetypeIds[1]), "sig-b-2");

        var result = await _service.RunOnceAsync(CancellationToken.None);

        Assert.Equal(4, result.FingerprintCount);
        Assert.True(result.WeightsComputed);
        Assert.True(result.ClustersUsed >= 2);
        Assert.True(result.ArchetypesRefined >= 2);

        var stored = await _store.GetGlobalWeightsAsync();
        Assert.NotNull(stored);
        Assert.Equal(dim, stored!.Value.Weights.Length);
    }

    private static Fingerprint MakeFingerprint(string id, float[] centroid, string clientType)
    {
        var weights = new float[centroid.Length];
        Array.Fill(weights, 1.0f);
        var now = DateTime.UtcNow;
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = (float[])centroid.Clone(),
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            ArchetypeOrigin = clientType,
            InferredClientType = clientType,
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            CachedBotProbability = 0.0,
            CachedRiskBand = null,
            CachedScoreUpdatedAt = null
        };
    }

    private static float[] MakeUnitVector(int dim, int seed)
    {
        var rng = new Random(seed);
        var v = new float[dim];
        for (var i = 0; i < dim; i++) v[i] = (float)rng.NextDouble();
        double sum = 0;
        for (var i = 0; i < dim; i++) sum += v[i] * v[i];
        var norm = (float)Math.Sqrt(sum);
        if (norm > 0)
            for (var i = 0; i < dim; i++) v[i] /= norm;
        return v;
    }
}
