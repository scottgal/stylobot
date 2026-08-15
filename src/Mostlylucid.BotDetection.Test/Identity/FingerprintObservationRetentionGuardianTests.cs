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

    private BotDetectionOptions Options(int maxObs, int driftCap = 5000) => new()
    {
        DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
        Identity = new IdentityOptions
        {
            Enabled = true,
            MaxObservationsPerFingerprint = maxObs,
            DriftMaxRowsPerArchetype = driftCap
        }
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

    private static Mostlylucid.BotDetection.Identity.BrowserModes.SqliteFingerprintBrowserModeStore NewModeStore(
        SqliteFingerprintStore store, BotDetectionOptions opts)
        => new(
            store,
            Microsoft.Extensions.Options.Options.Create(opts),
            NullLogger<Mostlylucid.BotDetection.Identity.BrowserModes.SqliteFingerprintBrowserModeStore>.Instance);

    private static FingerprintObservationRetentionGuardian NewGuardian(
        SqliteFingerprintStore store, BotDetectionOptions opts)
        => new(
            store,
            NewModeStore(store, opts),
            Microsoft.Extensions.Options.Options.Create(opts),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<FingerprintObservationRetentionGuardian>.Instance);

    [Fact]
    public async Task GuardAsync_prunes_older_absorbed_but_floors_keep_at_the_drift_cap()
    {
        // The drift reader ranks the newest 5 rows per archetype (driftCap: 5), but
        // retention is naively configured to keep only 2. The guardian MUST floor its
        // effective keep-count at the drift cap, or the prune would delete 3 rows that
        // ListRecentObservationsForDriftAsync ranks (both readers scan absorbed rows
        // unfiltered). Small caps here so the prune genuinely runs, unlike the 5000
        // default where a handful of rows is a no-op.
        var opts = Options(maxObs: 2, driftCap: 5);
        var store = await NewStoreAsync(opts);
        await SeedFingerprintAsync(store, "fp");

        // 8 absorbed observations (same archetype "chrome-desktop" / ua "chrome").
        for (var i = 0; i < 8; i++)
            await SeedObservationRowAsync(store, "fp");
        await MarkAllObservationsAbsorbedAsync("fp");

        // Baseline: the drift reader ranks a full cap of 5 rows.
        var driftBefore = await store.ListRecentObservationsForDriftAsync(opts.Identity.DriftMaxRowsPerArchetype);
        Assert.Equal(5, driftBefore.Count);

        var report = await NewGuardian(store, opts).GuardAsync();

        // The prune actually RAN (8 -> 5, not a no-op) and kept exactly the drift cap:
        // the 3 oldest absorbed rows are gone, but every row the drift reader ranks
        // survives. If effectiveK were the naive 2 (floor broken), only 2 would remain
        // and the drift reader would be starved to 2 rows.
        Assert.Equal("pruned", report.Status);
        Assert.Equal(5, await TotalObservationsAsync("fp"));

        var driftAfter = await store.ListRecentObservationsForDriftAsync(opts.Identity.DriftMaxRowsPerArchetype);
        Assert.Equal(5, driftAfter.Count);
    }

    [Fact]
    public async Task GuardAsync_always_keeps_unabsorbed_rows_even_below_the_keep_count()
    {
        // Unabsorbed rows are never eligible for pruning regardless of the keep count.
        var opts = Options(maxObs: 1, driftCap: 1);
        var store = await NewStoreAsync(opts);
        await SeedFingerprintAsync(store, "fp");

        // 4 absorbed then 3 unabsorbed. effectiveK = max(1, 1) = 1.
        for (var i = 0; i < 4; i++)
            await SeedObservationRowAsync(store, "fp");
        await MarkAllObservationsAbsorbedAsync("fp");
        for (var i = 0; i < 3; i++)
            await SeedObservationRowAsync(store, "fp");

        await NewGuardian(store, opts).GuardAsync();

        // All 3 unabsorbed survive; absorbed pruned down to the keep of 1.
        Assert.Equal(3, await UnabsorbedCountAsync("fp"));
        Assert.Equal(4, await TotalObservationsAsync("fp")); // 3 unabsorbed + 1 kept absorbed
    }

    [Fact]
    public async Task GuardAsync_reports_pruned_status_and_row_counts()
    {
        var opts = Options(maxObs: 5000);
        var store = await NewStoreAsync(opts);
        await SeedFingerprintAsync(store, "fp");
        for (var i = 0; i < 4; i++)
            await SeedObservationRowAsync(store, "fp");
        await MarkAllObservationsAbsorbedAsync("fp");

        var report = await NewGuardian(store, opts).GuardAsync();

        Assert.Equal("pruned", report.Status);
        Assert.Equal(GuardianCategory.Data, report.Category);
        Assert.Equal("FingerprintObservationRetention", report.GuardianName);
        // Under the 5000 keep everything survives (drift-preserving no-op prune).
        Assert.Equal(4, await TotalObservationsAsync("fp"));
    }

    [Fact]
    public async Task GuardAsync_prunes_absorbed_mode_observations_beyond_keep_but_never_unabsorbed()
    {
        // A soak measured fingerprint_mode_observations at ~71% of the identity DB's
        // growth: one row per resolved mode per request, absorbed-but-never-deleted.
        // The guardian prunes absorbed mode rows beyond the keep just as it does the
        // plain observations table, while unabsorbed rows (the drainer's only reader
        // filters absorbed_at IS NULL) always survive.
        var opts = Options(maxObs: 2, driftCap: 2); // effectiveK = 2
        var store = await NewStoreAsync(opts);
        await SeedFingerprintAsync(store, "fp");
        var modeStore = NewModeStore(store, opts);

        // 6 absorbed then 3 unabsorbed mode observations (seeded directly — Phase B:
        // RecordModeObservationAsync is memory-only; the guardian's prune serves
        // legacy rows).
        for (var i = 0; i < 6; i++)
            await SeedModeObservationRowAsync(store, "fp", "mode-a");
        await MarkAllModeObservationsAbsorbedAsync("fp");
        for (var i = 0; i < 3; i++)
            await SeedModeObservationRowAsync(store, "fp", "mode-a");

        var report = await NewGuardian(store, opts).GuardAsync();

        // effectiveK = 2 absorbed kept + 3 unabsorbed always kept = 5 total; the 4
        // oldest absorbed rows are pruned. The report details name the mode count.
        Assert.Equal("pruned", report.Status);
        Assert.Equal(3, await UnabsorbedModeCountAsync("fp"));
        Assert.Equal(5, await TotalModeObservationsAsync("fp"));
        Assert.Contains("mode observations", report.Details);
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

    // ── Phase B seeding (write-path grain redesign): RecordObservationAsync /
    // RecordModeObservationAsync are memory-only; the retention guardian's prune
    // mechanisms serve LEGACY rows, so observations are seeded directly. ────────

    private async Task SeedObservationRowAsync(SqliteFingerprintStore store, string fpId)
    {
        var blob = SqliteFingerprintStore.FloatsToBlob(UnitVector());
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_observations (fingerprint_id, vector, observed_at, absorbed_at, ua_family)
            VALUES (@fp, @vec, @ts, NULL, 'chrome');
            UPDATE fingerprints
               SET observation_count = observation_count + 1,
                   last_seen = @ts
             WHERE fingerprint_id = @fp;
            """;
        cmd.Parameters.AddWithValue("@fp", fpId);
        cmd.Parameters.AddWithValue("@vec", blob);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task SeedModeObservationRowAsync(
        SqliteFingerprintStore store, string fpId, string modeId)
    {
        var blob = SqliteFingerprintStore.FloatsToBlob(UnitVector());
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_mode_observations (fingerprint_id, mode_id, vector, observed_at, absorbed_at, ua_family)
            VALUES (@fp, @mode, @vec, @ts, NULL, 'chrome');
            """;
        cmd.Parameters.AddWithValue("@fp", fpId);
        cmd.Parameters.AddWithValue("@mode", modeId);
        cmd.Parameters.AddWithValue("@vec", blob);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

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

    private async Task MarkAllModeObservationsAbsorbedAsync(string fingerprintId)
    {
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprint_mode_observations
               SET absorbed_at = @ts
             WHERE fingerprint_id = @id AND absorbed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync();
    }

    private Task<int> TotalModeObservationsAsync(string id) =>
        ScalarAsync("SELECT COUNT(*) FROM fingerprint_mode_observations WHERE fingerprint_id = @id", id);

    private Task<int> UnabsorbedModeCountAsync(string id) =>
        ScalarAsync(
            "SELECT COUNT(*) FROM fingerprint_mode_observations WHERE fingerprint_id = @id AND absorbed_at IS NULL",
            id);
}
