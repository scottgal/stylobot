using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     <see cref="FingerprintObservationRetentionGuardian"/> (Part B / Task 9):
///     the drift-preserving absorbed-observation prune. The critical invariant is
///     that the guardian's effective keep-count is floored at the drift reader's
///     per-archetype cap, so rows that <see cref="SqliteFingerprintStore.ListRecentObservationsForDriftAsync"/>
///     would rank always survive a prune.
/// </summary>
public class FingerprintObservationRetentionGuardianTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fpDb;
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    public FingerprintObservationRetentionGuardianTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fp-obs-retention-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _fpDb = Path.Combine(_tempDir, "fingerprints.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private BotDetectionOptions Options(int maxObs) => new()
    {
        DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
        Identity = new IdentityOptions { Enabled = true, MaxObservationsPerFingerprint = maxObs }
    };

    private async Task<SqliteFingerprintStore> NewStoreAsync(BotDetectionOptions opts)
    {
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            Microsoft.Extensions.Options.Options.Create(opts),
            IdentityVectorLayout.DefaultV1());
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static FingerprintObservationRetentionGuardian NewGuardian(
        SqliteFingerprintStore store, BotDetectionOptions opts)
        => new(
            store,
            Microsoft.Extensions.Options.Options.Create(opts),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<FingerprintObservationRetentionGuardian>.Instance);

    [Fact]
    public async Task GuardAsync_preserves_drift_rankable_absorbed_rows()
    {
        var opts = Options(maxObs: 2); // configured below the drift cap on purpose
        var store = await NewStoreAsync(opts);
        await SeedFingerprintAsync(store, "fp");

        // 8 absorbed (older) + 3 unabsorbed (newer). With the configured keep of 2,
        // a naive prune would delete 6 absorbed. But effectiveK is floored at the
        // drift cap (5000), so every absorbed row the drift reader ranks survives.
        for (var i = 0; i < 8; i++)
            await store.RecordObservationAsync(RequestScope.Unknown, "fp", UnitVector(), "chrome");
        await MarkAllObservationsAbsorbedAsync("fp");
        for (var i = 0; i < 3; i++)
            await store.RecordObservationAsync(RequestScope.Unknown, "fp", UnitVector(), "chrome");

        var driftBefore = await store.ListRecentObservationsForDriftAsync(
            IdentityWeightCalibrationService.DriftMaxRowsPerArchetype);
        Assert.NotEmpty(driftBefore);

        var report = await NewGuardian(store, opts).GuardAsync();

        // Drift-preservation: the guard floored keep at the drift cap, so nothing
        // the drift reader ranks was pruned.
        Assert.Equal("pruned", report.Status);
        Assert.Equal(11, await TotalObservationsAsync("fp"));
        Assert.Equal(3, await UnabsorbedCountAsync("fp"));

        var driftAfter = await store.ListRecentObservationsForDriftAsync(
            IdentityWeightCalibrationService.DriftMaxRowsPerArchetype);
        Assert.Equal(driftBefore.Count, driftAfter.Count);
    }

    [Fact]
    public async Task GuardAsync_reports_pruned_status_and_row_counts()
    {
        var opts = Options(maxObs: 5000);
        var store = await NewStoreAsync(opts);
        await SeedFingerprintAsync(store, "fp");
        for (var i = 0; i < 4; i++)
            await store.RecordObservationAsync(RequestScope.Unknown, "fp", UnitVector(), "chrome");
        await MarkAllObservationsAbsorbedAsync("fp");

        var report = await NewGuardian(store, opts).GuardAsync();

        Assert.Equal("pruned", report.Status);
        Assert.Equal(GuardianCategory.Data, report.Category);
        Assert.Equal("FingerprintObservationRetention", report.GuardianName);
        // Under the 5000 keep everything survives (drift-preserving no-op prune).
        Assert.Equal(4, await TotalObservationsAsync("fp"));
    }

    [Fact]
    public void Guardian_identity_is_data_category()
    {
        var opts = Options(maxObs: 5000);
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            Microsoft.Extensions.Options.Options.Create(opts),
            IdentityVectorLayout.DefaultV1());
        var guardian = NewGuardian(store, opts);

        Assert.Equal("FingerprintObservationRetention", guardian.Name);
        Assert.Equal(GuardianCategory.Data, guardian.Category);
        Assert.True(guardian.Enabled);
    }

    // ============================================================
    // Helpers
    // ============================================================

    private static float[] UnitVector()
    {
        var v = new float[Dim];
        v[0] = 1.0f;
        return v;
    }

    private async Task SeedFingerprintAsync(SqliteFingerprintStore store, string id)
    {
        var now = DateTime.UtcNow;
        var weights = new float[Dim];
        Array.Fill(weights, 1.0f);
        await store.InsertFingerprintAsync(new Fingerprint
        {
            FingerprintId = id,
            Centroid = new float[Dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 0,
            CorrectionCount = 0,
            FirstSeen = now.AddHours(-1),
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            ClaimStatus = "unverified"
        }, $"sig-{id}");
    }

    private async Task MarkAllObservationsAbsorbedAsync(string fingerprintId)
    {
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprint_observations
               SET absorbed_at = @ts
             WHERE fingerprint_id = @id AND absorbed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> ScalarAsync(string sql, string fingerprintId)
    {
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private Task<int> TotalObservationsAsync(string id) =>
        ScalarAsync("SELECT COUNT(*) FROM fingerprint_observations WHERE fingerprint_id = @id", id);

    private Task<int> UnabsorbedCountAsync(string id) =>
        ScalarAsync(
            "SELECT COUNT(*) FROM fingerprint_observations WHERE fingerprint_id = @id AND absorbed_at IS NULL",
            id);
}
