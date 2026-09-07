using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Full learning-cycle coverage for the delta-streaming absorption model
///     (reference_centroid_delta_streaming_absorption). The unit tests in
///     <c>FingerprintDeltaNoveltyTests</c> drive the detached sampler's persist
///     method DIRECTLY. That leaves the cadence WIRING unproven: if the real
///     fold-time loop stopped invoking the sampler, every one of those tests
///     would stay green while the delta never reached SQLite. These tests close
///     that gap by letting the REAL detached loop run on a fast, configurable
///     cadence (<see cref="IdentityDriftOptions.FoldTimeEvaluationIntervalMs"/>)
///     and asserting the durable row converges WITHOUT the test calling the
///     sampler itself.
///
///     <para>
///         This is the same "drive the loop as a loop, never sleep for a real
///         heartbeat" idiom as <see cref="CentroidLearningLoopTests"/> and
///         <see cref="AdaptiveTriggerEndToEndTests"/> — the cadence knob is set
///         to tens of milliseconds so the full stable→delta→durable cycle
///         completes in test-time, not the 500 ms production default and never
///         the 30-minute calibration-scale gates.
///     </para>
/// </summary>
public sealed class DeltaStreamingLearningCycleTests : IDisposable
{
    private readonly string _tempDir;

    public DeltaStreamingLearningCycleTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-delta-cycle-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    private string FingerprintsDbPath => Path.Combine(_tempDir, "fingerprints.db");

    /// <summary>A single controlled archetype whose centroid we feed back (Mahalanobis ≈ 0).</summary>
    private static IdentityArchetype MakeChromeArchetype()
    {
        var centroid = new float[Dim];
        var mask = new float[Dim];
        Array.Fill(mask, 1f);
        var variance = new float[Dim];
        Array.Fill(variance, 1f);
        return new IdentityArchetype
        {
            ArchetypeId = "fixture-chrome",
            Name = "fixture-chrome",
            ArchetypeKind = "test",
            Centroid = centroid,
            DimensionMask = mask,
            VarianceVector = variance,
            VarianceMultiplier = 1.0,
        };
    }

    private async Task<(SqliteFingerprintStore Store, IdentityArchetype Fixture)> BuildAsync(
        int foldTimeIntervalMs)
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
                    MahalanobisNoveltyThreshold = 8.0,
                    Durable = true,
                },
                Drift = new IdentityDriftOptions
                {
                    // The REAL cadence the detached sampler runs on — tens of ms so the
                    // test completes fast without sleeping for the 500 ms default.
                    FoldTimeEvaluationIntervalMs = foldTimeIntervalMs,
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
        var archetypes = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
        var fixture = MakeChromeArchetype();
        archetypes.Replace(new[] { fixture });

        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, layout, archetypes: archetypes);
        await store.EnsureInitialisedAsync();
        return (store, fixture);
    }

    private static async Task SeedFingerprintAsync(SqliteFingerprintStore store, string fpId)
    {
        var weights = new float[Dim];
        Array.Fill(weights, 1.0f);
        var now = DateTime.UtcNow;
        var fp = new Fingerprint
        {
            FingerprintId = fpId,
            Centroid = new float[Dim],
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
    }

    /// <summary>
    ///     Poll the DURABLE row (raw SQLite — deliberately NOT the LFU) until the
    ///     delta column reaches <paramref name="expectedCount"/>, or the timeout
    ///     elapses. Returns the durable delta_count observed at the last read.
    ///     Reading SQLite directly proves persistence: the in-memory LFU is
    ///     authoritative on the hot path and would report the delta even if the
    ///     durable write never happened.
    /// </summary>
    private async Task<int> PollDurableDeltaCountAsync(
        string fpId, int expectedCount, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        var lastSeen = 0;
        do
        {
            await using var conn = new SqliteConnection($"Data Source={FingerprintsDbPath};Pooling=true");
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT delta_count FROM fingerprints WHERE fingerprint_id = @id
                """;
            cmd.Parameters.AddWithValue("@id", fpId);
            var scalar = await cmd.ExecuteScalarAsync();
            lastSeen = scalar is null ? 0 : Convert.ToInt32(scalar);
            if (lastSeen >= expectedCount) return lastSeen;
            await Task.Delay(25);
        }
        while (DateTime.UtcNow < deadline);
        return lastSeen;
    }

    /// <summary>
    ///     The full cycle, driven by the REAL detached fold-time loop: observations
    ///     fold in memory on the request path (delta accumulates against the
    ///     archetype base), and the detached sampler — running on its own fast
    ///     cadence — compresses that current state to the durable row WITHOUT any
    ///     test calling the persist method. If the loop's sampler invocation were
    ///     removed, this test fails even though every unit test stays green.
    /// </summary>
    [Fact]
    [Trait("Category", "Learning")]
    public async Task RealFoldTimeLoop_PersistsAccumulatedDelta_WithoutDirectPersistCall()
    {
        var (store, fixture) = await BuildAsync(foldTimeIntervalMs: 25);
        var fpId = "fp-cycle-1";
        await SeedFingerprintAsync(store, fpId);
        // Warm the LFU so the fold runs against a resident entry (the real path).
        await store.GetFingerprintAsync(fpId);

        // Five confirmatory observations of the archetype centroid: the in-memory
        // fold advances delta_count 0 → 5 against the fixture base.
        for (var i = 0; i < 5; i++)
            await store.RecordObservationAsync(
                RequestScope.Unknown, fpId, fixture.Centroid, "chrome-desktop", CancellationToken.None);

        // The REAL loop must persist it — poll the durable row, never the LFU.
        var durableCount = await PollDurableDeltaCountAsync(fpId, expectedCount: 5,
            TimeSpan.FromSeconds(10));
        Assert.True(durableCount >= 5,
            $"real fold-time loop did not persist the accumulated delta: durable delta_count={durableCount} " +
            $"after 5 confirmatory observations (expected ≥5). The loop's sampler invocation is broken.");

        // The durable row carries the current compact delta state, not just the count.
        await using var conn = new SqliteConnection($"Data Source={FingerprintsDbPath};Pooling=true");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT delta_count, novelty_count, delta_archetype_id,
                   delta_from_archetype IS NOT NULL
              FROM fingerprints WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", fpId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(5, reader.GetInt32(0));
        Assert.Equal(0, reader.GetInt32(1));
        Assert.Equal("fixture-chrome", reader.GetString(2));
        Assert.True(reader.GetBoolean(3), "the compact delta BLOB reached the durable row");
    }

    /// <summary>
    ///     Novelty also flows through the real loop: a beyond-threshold observation
    ///     is NOT smeared into the running-mean delta — it increments the durable
    ///     novelty_count so the periodic Leiden consolidator can see it.
    /// </summary>
    [Fact]
    [Trait("Category", "Learning")]
    public async Task RealFoldTimeLoop_PersistsNoveltyCount_ForBeyondThresholdObservation()
    {
        var (store, fixture) = await BuildAsync(foldTimeIntervalMs: 25);
        var fpId = "fp-cycle-novel";
        await SeedFingerprintAsync(store, fpId);
        await store.GetFingerprintAsync(fpId);

        // One confirmatory (to establish the archetype base), then a far point
        // pushed 10 variance-units past the base on dim 0 → Mahalanobis 10 ≥ 8.
        await store.RecordObservationAsync(
            RequestScope.Unknown, fpId, fixture.Centroid, "chrome-desktop", CancellationToken.None);
        var far = (float[])fixture.Centroid.Clone();
        far[0] += 10f;
        await store.RecordObservationAsync(
            RequestScope.Unknown, fpId, far, "chrome-desktop", CancellationToken.None);

        var durableCount = await PollDurableDeltaCountAsync(fpId, expectedCount: 1,
            TimeSpan.FromSeconds(10));
        Assert.True(durableCount >= 1,
            $"real loop did not persist delta_count for the confirmatory observation (got {durableCount})");

        // The sampler writes delta + novelty in one pass; once the delta landed the
        // novelty column is durable too. Assert both from the row.
        await using var conn = new SqliteConnection($"Data Source={FingerprintsDbPath};Pooling=true");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT delta_count, novelty_count FROM fingerprints WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", fpId);
        await using var reader = await cmd.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetInt32(0) >= 1, "confirmatory observation folded into the delta");
        Assert.Equal(1, reader.GetInt32(1));
    }
}
