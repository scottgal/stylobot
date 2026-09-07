using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Delta-streaming absorption tests
///     (reference_centroid_delta_streaming_absorption). Covers the per-encounter
///     Mahalanobis novelty gate, the compact running-mean delta save (never the full
///     vector), and the coalesced write-behind durability seam.
/// </summary>
public sealed class FingerprintDeltaNoveltyTests : IDisposable
{
    private readonly string _tempDir;

    public FingerprintDeltaNoveltyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-delta-novelty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private string ConnectionString
        => $"Data Source={Path.Combine(_tempDir, "fingerprints.db")};Pooling=true";

    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    /// <summary>A single fixed archetype whose centroid/variance we fully control.</summary>
    private static IdentityArchetype MakeArchetype(string id, float centroidValue = 0f, float varianceValue = 1f)
    {
        var centroid = new float[Dim];
        Array.Fill(centroid, centroidValue);
        var mask = new float[Dim];
        Array.Fill(mask, 1f); // assert every dim so EffectiveVarianceFor is deterministic
        var variance = new float[Dim];
        Array.Fill(variance, varianceValue);
        return new IdentityArchetype
        {
            ArchetypeId = id,
            Name = id,
            ArchetypeKind = "test",
            Centroid = centroid,
            DimensionMask = mask,
            VarianceVector = variance,
            VarianceMultiplier = 1.0,
        };
    }

    private async Task<(SqliteFingerprintStore Store, IdentityArchetypeRegistry Archetypes, IdentityArchetype Fixture)>
        BuildEnvAsync(double threshold, bool durable = true)
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Delta = new DeltaNoveltyOptions
                {
                    Enabled = true,
                    MahalanobisNoveltyThreshold = threshold,
                    Durable = durable,
                },
                Drift = new IdentityDriftOptions
                {
                    // Keep the background fold-time loop from racing the durability asserts.
                    FoldTimeEvaluationIntervalMs = 60_000,
                },
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = 1,
                    AbsorptionAgeDays = 30,
                    ActiveWindowDays = 90,
                    SubscriptionDebounceMs = 50,
                },
            },
        });
        var layout = IdentityVectorLayout.DefaultV1();
        var encoder = new IdentityVectorEncoder(layout);
        var archetypes = new IdentityArchetypeRegistry(NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
        var fixture = MakeArchetype("fixture-chrome");
        archetypes.Replace(new[] { fixture });
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, layout, archetypes: archetypes);
        await store.EnsureInitialisedAsync();
        return (store, archetypes, fixture);
    }

    private static async Task<string> SeedFingerprintAsync(SqliteFingerprintStore store, string fpId)
    {
        var dim = store.Layout.Dimension;
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        var fp = new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "fixture-chrome",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
        };
        await store.InsertFingerprintAsync(fp, $"sig-{fpId}", CancellationToken.None);
        await store.GetFingerprintAsync(fpId, CancellationToken.None); // warm the LFU so the fold runs
        return fpId;
    }

    // ── Novelty gate ────────────────────────────────────────────────────────

    [Fact]
    public async Task MahalanobisGate_WithinThreshold_AccumulatesDelta_NotNovelty()
    {
        var (store, _, fixture) = await BuildEnvAsync(threshold: 8.0);
        var fpId = await SeedFingerprintAsync(store, "fp-within-1");

        // Exactly the archetype centroid → Mahalanobis ≈ 0 → confirmatory.
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, fixture.Centroid, "chrome-desktop", CancellationToken.None);
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, fixture.Centroid, "chrome-desktop", CancellationToken.None);

        var fp = await store.GetFingerprintAsync(fpId, CancellationToken.None);
        fp!.DeltaCount.Should().Be(2, "two within-threshold observations fold into the delta");
        fp.NoveltyCount.Should().Be(0, "within-threshold observations are not novel");
        fp.DeltaFromArchetype.Should().NotBeNull();
        fp.DeltaArchetypeId.Should().Be("fixture-chrome");
        // delta = mean(obs) − centroid = centroid − centroid = 0
        fp.DeltaFromArchetype!.All(d => Math.Abs(d) < 1e-6).Should().BeTrue("delta from the exact centroid is zero");
    }

    [Fact]
    public async Task MahalanobisGate_BeyondThreshold_CountsNovelty_NotDelta()
    {
        var (store, _, fixture) = await BuildEnvAsync(threshold: 8.0);
        var fpId = await SeedFingerprintAsync(store, "fp-novel-1");

        // A far observation (single dim pushed well past the archetype's variance 1 → dist 10 ≥ 8).
        var far = (float[])fixture.Centroid.Clone();
        far[0] += 10f;
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, far, "chrome-desktop", CancellationToken.None);

        var fp = await store.GetFingerprintAsync(fpId, CancellationToken.None);
        fp!.NoveltyCount.Should().Be(1, "a beyond-threshold observation is genuinely novel");
        fp.DeltaCount.Should().Be(0, "a novel observation does not smear the compact delta");
    }

    [Fact]
    public void MahalanobisGate_PureMath_PinsWithinVsBeyond()
    {
        var (store, _, _) = BuildEnvAsync(threshold: 8.0).GetAwaiter().GetResult();
        var fixture = MakeArchetype("fixture-chrome");

        // Variance 1.0 across every dim. distance(centroid, centroid) ≈ 0 (within).
        var distanceSame = store.ComputeDeltaNoveltyDistance(fixture.Centroid, fixture);
        distanceSame.Should().NotBeNull();
        distanceSame!.Value.Should().BeLessThan(8.0);
        store.IsNovelObservation(fixture.Centroid, fixture).Should().BeFalse();

        // A vector with a single dim displaced by 10: distance = sqrt(10^2 / 1.0) = 10 ≥ 8 (beyond).
        var far = (float[])fixture.Centroid.Clone();
        far[0] += 10f;
        var distanceFar = store.ComputeDeltaNoveltyDistance(far, fixture);
        distanceFar.Should().NotBeNull();
        distanceFar!.Value.Should().BeGreaterThanOrEqualTo(8.0);
        store.IsNovelObservation(far, fixture).Should().BeTrue();
    }

    // ── Compact delta save ──────────────────────────────────────────────────

    [Fact]
    public async Task CompactDelta_IsRunningMeanOfResiduals_NotTheFullVector()
    {
        var (store, _, fixture) = await BuildEnvAsync(threshold: 8.0);
        var fpId = await SeedFingerprintAsync(store, "fp-delta-mean-1");
        var centroid = fixture.Centroid;

        // Two confirmatory observations displaced +2 / +4 on dim 0 (each within variance 1 → dist 2/4 < 8).
        var obs1 = (float[])centroid.Clone();
        obs1[0] += 2f;
        var obs2 = (float[])centroid.Clone();
        obs2[0] += 4f;
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, obs1, "chrome-desktop", CancellationToken.None);
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, obs2, "chrome-desktop", CancellationToken.None);

        var fp = await store.GetFingerprintAsync(fpId, CancellationToken.None);
        fp!.DeltaCount.Should().Be(2);
        // delta = mean residual = ((obs1−c) + (obs2−c)) / 2 = (2 + 4)/2 = 3 on dim 0, 0 elsewhere.
        fp.DeltaFromArchetype.Should().NotBeNull();
        ((double)fp.DeltaFromArchetype![0]).Should().BeApproximately(3.0, 1e-4);
        for (var i = 1; i < Dim; i++)
            ((double)fp.DeltaFromArchetype[i]).Should().BeApproximately(0.0, 1e-4);

        // Phase B invariant preserved: nothing durable was written per-observation.
        (await store.GetUnabsorbedObservationCountAsync(fpId, CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task CompactDelta_Durable_SamplerPersistsCurrentStateOnce()
    {
        var (store, _, fixture) = await BuildEnvAsync(threshold: 8.0, durable: true);
        var fpId = await SeedFingerprintAsync(store, "fp-durable-1");

        // Multiple confirmatory observations advance the in-memory running-mean delta.
        for (var i = 0; i < 5; i++)
            await store.RecordObservationAsync(RequestScope.Unknown, fpId, fixture.Centroid, "chrome-desktop", CancellationToken.None);

        // One detached-sampler pass compresses the CURRENT in-memory state to the row —
        // a single batched write at persist time (never one per observation), driven
        // deterministically here instead of waiting on the background fold-time loop.
        var written = await store.PersistSampleDeltasAsync();
        written.Should().Be(1, "one pass persists the one advanced fingerprint");

        // The durable row carries the final delta columns (the pass wrote current state).
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT delta_count, novelty_count, delta_archetype_id,
                   delta_from_archetype IS NOT NULL
              FROM fingerprints WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", fpId);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(5);
        reader.GetInt32(1).Should().Be(0);
        reader.GetString(2).Should().Be("fixture-chrome");
        reader.GetBoolean(3).Should().BeTrue("the compact delta BLOB reached the durable row");

        // A second pass is a no-op: the watermark now matches durable state.
        (await store.PersistSampleDeltasAsync()).Should().Be(0, "nothing advanced since the last persist");
    }

    [Fact]
    public async Task DurableDisabled_SamplerPersistsNothing()
    {
        var (store, _, fixture) = await BuildEnvAsync(threshold: 8.0, durable: false);
        var fpId = await SeedFingerprintAsync(store, "fp-nondurable-1");
        await store.RecordObservationAsync(RequestScope.Unknown, fpId, fixture.Centroid, "chrome-desktop", CancellationToken.None);

        // Delta accumulated in-memory but durability off → the detached sampler is a no-op.
        var written = await store.PersistSampleDeltasAsync();
        written.Should().Be(0);

        // And the durable row is untouched (still null/zero delta columns).
        await using var conn = new SqliteConnection(ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT delta_count, novelty_count, delta_from_archetype IS NOT NULL
              FROM fingerprints WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", fpId);
        await using var reader = await cmd.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        reader.GetInt32(0).Should().Be(0);
        reader.GetInt32(1).Should().Be(0);
        reader.GetBoolean(2).Should().BeFalse("durability off means the delta never reaches the row");
    }
}
