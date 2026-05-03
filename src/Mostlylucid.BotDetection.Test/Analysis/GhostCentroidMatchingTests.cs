using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Test.Analysis;

/// <summary>
/// Tests for FindGhostCentroidsAsync — the FOSS ghost campaign matching path.
/// Ghost shapes are L1/L2 HNSW centroid entries created by VectorCompactionService.
/// </summary>
public class GhostCentroidMatchingTests : IAsyncLifetime
{
    private HnswSessionVectorSearch _index = null!;
    private static readonly int Dims = SessionVectorizer.Dimensions;

    public async Task InitializeAsync()
    {
        var opts = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"ghost-test-{Guid.NewGuid():N}")
        });
        _index = new HnswSessionVectorSearch(NullLogger<HnswSessionVectorSearch>.Instance, opts);
        await _index.LoadAsync();
    }

    public Task DisposeAsync()
    {
        _index.Dispose();
        return Task.CompletedTask;
    }

    // ============================================================
    // Empty index
    // ============================================================

    [Fact]
    public async Task FindGhostCentroidsAsync_EmptyIndex_ReturnsEmpty()
    {
        var results = await _index.FindGhostCentroidsAsync(UnitVector(0), topK: 5, minSimilarity: 0.75f);
        Assert.Empty(results);
    }

    // ============================================================
    // L0 entries are NOT ghost shapes
    // ============================================================

    [Fact]
    public async Task FindGhostCentroidsAsync_OnlyL0Entries_ReturnsEmpty()
    {
        // Add identical vector as L0 (individual session) — should not match
        await _index.AddAsync(UnitVector(0), "sig-l0", isBot: true, botProbability: 0.9);

        var results = await _index.FindGhostCentroidsAsync(UnitVector(0), topK: 5, minSimilarity: 0.0f);

        Assert.Empty(results);
    }

    // ============================================================
    // L1 entries ARE ghost shapes
    // ============================================================

    [Fact]
    public async Task FindGhostCentroidsAsync_IdenticalL1_ReturnsMatch()
    {
        var vec = UnitVector(0);
        await _index.ReplaceAllAsync([(vec, MakeMeta("sig-l1", compressionLevel: 1, isBot: true))]);

        var results = await _index.FindGhostCentroidsAsync(vec, topK: 5, minSimilarity: 0.75f);

        Assert.Single(results);
        Assert.Equal("sig-l1", results[0].FamilyId);
        Assert.Equal(1, results[0].CompressionLevel);
        Assert.True(results[0].IsBot);
    }

    [Fact]
    public async Task FindGhostCentroidsAsync_L2Entry_ReturnsMatch()
    {
        var vec = UnitVector(1);
        await _index.ReplaceAllAsync([(vec, MakeMeta("cluster-xyz", compressionLevel: 2, isBot: true, botProb: 0.92))]);

        var results = await _index.FindGhostCentroidsAsync(vec, topK: 5, minSimilarity: 0.75f);

        Assert.Single(results);
        Assert.Equal("cluster-xyz", results[0].FamilyId);
        Assert.Equal(2, results[0].CompressionLevel);
        Assert.Equal(0.92, results[0].BotProbability, 2);
    }

    // ============================================================
    // Similarity threshold filtering
    // ============================================================

    [Fact]
    public async Task FindGhostCentroidsAsync_OrthogonalVector_BelowThreshold_ReturnsEmpty()
    {
        // Add L1 in dim 0
        await _index.ReplaceAllAsync([(UnitVector(0), MakeMeta("sig-a", compressionLevel: 1))]);

        // Query in orthogonal dim — cosine similarity = 0
        var results = await _index.FindGhostCentroidsAsync(UnitVector(Dims - 1), topK: 5, minSimilarity: 0.75f);

        Assert.Empty(results);
    }

    [Fact]
    public async Task FindGhostCentroidsAsync_ZeroThreshold_ReturnsL1EvenIfOrthogonal()
    {
        await _index.ReplaceAllAsync([(UnitVector(0), MakeMeta("sig-b", compressionLevel: 1))]);

        // minSimilarity = 0 should return all L1/L2 regardless of distance
        var results = await _index.FindGhostCentroidsAsync(UnitVector(Dims - 1), topK: 5, minSimilarity: 0.0f);

        Assert.Single(results);
        Assert.Equal("sig-b", results[0].FamilyId);
    }

    // ============================================================
    // Mixed L0 + L1: only L1 returned
    // ============================================================

    [Fact]
    public async Task FindGhostCentroidsAsync_MixedIndex_OnlyL1L2Returned()
    {
        var vec = UnitVector(0);

        // L0 and L1 at the same position
        await _index.AddAsync(vec, "l0-sig", isBot: false, botProbability: 0.1);
        await _index.ReplaceAllAsync([(vec, MakeMeta("l1-sig", compressionLevel: 1, isBot: true))]);

        var results = await _index.FindGhostCentroidsAsync(vec, topK: 5, minSimilarity: 0.0f);

        Assert.All(results, r => Assert.True(r.CompressionLevel >= 1));
        Assert.DoesNotContain(results, r => r.FamilyId == "l0-sig");
    }

    // ============================================================
    // topK limit
    // ============================================================

    [Fact]
    public async Task FindGhostCentroidsAsync_TopKLimit_ReturnsOnlyTopK()
    {
        // Add 5 L1 entries all pointing in the same direction (will all have similarity ~ 1)
        var entries = Enumerable.Range(0, 5)
            .Select(i => (UnitVector(0), MakeMeta($"sig-{i}", compressionLevel: 1)))
            .ToList();
        await _index.ReplaceAllAsync(entries);

        var results = await _index.FindGhostCentroidsAsync(UnitVector(0), topK: 3, minSimilarity: 0.0f);

        Assert.Equal(3, results.Count);
    }

    // ============================================================
    // Metadata preservation
    // ============================================================

    [Fact]
    public async Task FindGhostCentroidsAsync_PreservesFrequencyFingerprint()
    {
        var vec = UnitVector(0);
        var fingerprint = new float[] { 0.9f, 0.1f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f };

        var meta = new SessionVectorMetadata
        {
            Signature = "sig-with-fp",
            IsBot = true,
            BotProbability = 0.85,
            Timestamp = DateTimeOffset.UtcNow,
            CompressionLevel = 1,
            FrequencyFingerprint = fingerprint
        };
        await _index.ReplaceAllAsync([(vec, meta)]);

        var results = await _index.FindGhostCentroidsAsync(vec, topK: 5, minSimilarity: 0.75f);

        Assert.Single(results);
        Assert.NotNull(results[0].FrequencyFingerprint);
        Assert.Equal(fingerprint.Length, results[0].FrequencyFingerprint!.Length);
        Assert.Equal(0.9f, results[0].FrequencyFingerprint[0], 3);
    }

    [Fact]
    public async Task FindGhostCentroidsAsync_PreservesVarianceVector()
    {
        var vec = UnitVector(0);
        var variance = Enumerable.Repeat(0.05f, Dims).ToArray();

        var meta = new SessionVectorMetadata
        {
            Signature = "sig-with-var",
            IsBot = true,
            BotProbability = 0.9,
            Timestamp = DateTimeOffset.UtcNow,
            CompressionLevel = 1,
            VarianceVector = variance
        };
        await _index.ReplaceAllAsync([(vec, meta)]);

        var results = await _index.FindGhostCentroidsAsync(vec, topK: 5, minSimilarity: 0.75f);

        Assert.Single(results);
        Assert.NotNull(results[0].VarianceVector);
        Assert.Equal(Dims, results[0].VarianceVector!.Length);
    }

    // ============================================================
    // Sorted by similarity descending
    // ============================================================

    [Fact]
    public async Task FindGhostCentroidsAsync_ResultsOrderedBySimilarityDescending()
    {
        // Two L1 entries: one close (dim 0 + tiny perturbation), one far (dim 1)
        var close = UnitVector(0);
        var perturbed = close.ToArray();
        perturbed[1] += 0.3f;
        var norm = MathF.Sqrt(perturbed.Sum(v => v * v));
        for (var i = 0; i < perturbed.Length; i++) perturbed[i] /= norm;

        await _index.ReplaceAllAsync([
            (close, MakeMeta("sig-close", compressionLevel: 1)),
            (UnitVector(1), MakeMeta("sig-far", compressionLevel: 1))
        ]);

        var results = await _index.FindGhostCentroidsAsync(UnitVector(0), topK: 5, minSimilarity: 0.0f);

        // First result should be closer to query (UnitVector(0))
        Assert.True(results[0].Similarity >= results[^1].Similarity,
            "Results should be ordered highest similarity first");
        // The entry at dim 0 should be first
        Assert.Equal("sig-close", results[0].FamilyId);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static float[] UnitVector(int dimension)
    {
        var v = new float[Dims];
        v[dimension] = 1.0f;
        return v;
    }

    private static SessionVectorMetadata MakeMeta(
        string signature, int compressionLevel, bool isBot = true, double botProb = 0.85)
        => new()
        {
            Signature = signature,
            IsBot = isBot,
            BotProbability = botProb,
            Timestamp = DateTimeOffset.UtcNow,
            CompressionLevel = compressionLevel
        };
}
