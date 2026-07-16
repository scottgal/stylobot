using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Durable-bounding primitives on <see cref="SqliteFingerprintStore"/> (identity
///     data guardians, Part B / Task 8): the fingerprint count, the oldest-first
///     priority candidate scan, the cascading cross-table delete the eviction
///     guardian uses, and the drift-preserving absorbed-observation prune the
///     observation-retention guardian uses.
///     <para>
///     Mirrors <c>SqliteSignatureEvictionTests</c> on the archive side: a real
///     <see cref="SqliteFingerprintStore"/> over a temp <c>fingerprints.db</c>,
///     seeded through the public write path plus targeted raw-SQL column stamps so
///     the score inputs (<c>cached_bot_probability</c> / <c>cached_risk_band</c> /
///     <c>last_seen</c> / <c>claim_status</c>) are deterministic.
///     </para>
/// </summary>
public class FingerprintStoreBoundingApiTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fpDb;
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    public FingerprintStoreBoundingApiTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fp-bounding-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _fpDb = Path.Combine(_tempDir, "fingerprints.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private async Task<SqliteFingerprintStore> NewStoreAsync()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            options,
            IdentityVectorLayout.DefaultV1());
        await store.EnsureInitialisedAsync();
        return store;
    }

    // ============================================================
    // GetFingerprintCountAsync
    // ============================================================

    [Fact]
    public async Task GetFingerprintCountAsync_counts_distinct_fingerprints()
    {
        var store = await NewStoreAsync();
        Assert.Equal(0, await store.GetFingerprintCountAsync());

        await SeedFingerprintAsync(store, "fp-a");
        await SeedFingerprintAsync(store, "fp-b");
        await SeedFingerprintAsync(store, "fp-c");

        Assert.Equal(3, await store.GetFingerprintCountAsync());
    }

    // ============================================================
    // GetAllFingerprintPriorityInfoAsync
    // ============================================================

    [Fact]
    public async Task GetAllFingerprintPriorityInfoAsync_returns_oldest_first_within_limit()
    {
        var store = await NewStoreAsync();
        var now = DateTime.UtcNow;
        await SeedFingerprintAsync(store, "newest", lastSeen: now);
        await SeedFingerprintAsync(store, "middle", lastSeen: now.AddDays(-5));
        await SeedFingerprintAsync(store, "oldest", lastSeen: now.AddDays(-30));

        var top2 = await store.GetAllFingerprintPriorityInfoAsync(limit: 2);

        Assert.Equal(2, top2.Count);
        Assert.Equal("oldest", top2[0].FingerprintId);
        Assert.Equal("middle", top2[1].FingerprintId);
    }

    [Fact]
    public async Task GetAllFingerprintPriorityInfoAsync_carries_score_inputs_and_protected_flag()
    {
        var store = await NewStoreAsync();
        var verifiedAt = new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc);
        await SeedFingerprintAsync(
            store, "risky", botProb: 0.91, riskBand: "VeryHigh",
            claimStatus: "verified", lastSeen: verifiedAt);

        var info = (await store.GetAllFingerprintPriorityInfoAsync(limit: 10)).Single();

        Assert.Equal("risky", info.FingerprintId);
        Assert.Equal(0.91, info.BotProbability, 4);
        Assert.Equal("VeryHigh", info.RiskBand);
        Assert.True(info.Protected); // claim_status == 'verified'
    }

    [Fact]
    public async Task GetAllFingerprintPriorityInfoAsync_unverified_is_not_protected()
    {
        var store = await NewStoreAsync();
        await SeedFingerprintAsync(store, "plain", claimStatus: "unverified");

        var info = (await store.GetAllFingerprintPriorityInfoAsync(limit: 10)).Single();

        Assert.False(info.Protected);
    }

    // ============================================================
    // DeleteFingerprintsAsync
    // ============================================================

    [Fact]
    public async Task DeleteFingerprintsAsync_removes_fingerprint_and_leaves_no_orphans()
    {
        var store = await NewStoreAsync();

        await SeedFingerprintAsync(store, "keep");
        await store.RecordObservationAsync(RequestScope.Unknown, "keep", UnitVector(), "chrome");
        await store.UpsertKeyAsync("sig-keep", "keep");

        await SeedFingerprintAsync(store, "drop");
        await store.RecordObservationAsync(RequestScope.Unknown, "drop", UnitVector(), "chrome");
        await store.RecordObservationAsync(RequestScope.Unknown, "drop", UnitVector(), "chrome");
        await store.UpsertKeyAsync("sig-drop", "drop");
        await store.RecordCorrectionAsync(
            "req-1", "sig-drop", null, "drop", UnitVector(), UnitVector());

        var deleted = await store.DeleteFingerprintsAsync(new[] { "drop" });

        Assert.Equal(1, deleted);
        Assert.Equal(1, await store.GetFingerprintCountAsync());

        // Zero orphan rows in every per-fingerprint table.
        Assert.Equal(0, await RowCountForFingerprintAsync("fingerprint_observations", "fingerprint_id", "drop"));
        Assert.Equal(0, await RowCountForFingerprintAsync("fingerprint_keys", "fingerprint_id", "drop"));
        Assert.Equal(0, await RowCountForFingerprintAsync("fingerprint_corrections", "pass2_fingerprint", "drop"));
        Assert.Equal(0, await RowCountForFingerprintAsync("fingerprint_root_history", "fingerprint_id", "drop"));
        Assert.Equal(0, await RowCountForFingerprintAsync("fingerprint_name_history", "fingerprint_id", "drop"));

        // Untouched fingerprint keeps its rows.
        Assert.Equal(1, await RowCountForFingerprintAsync("fingerprint_observations", "fingerprint_id", "keep"));
        Assert.Equal(1, await RowCountForFingerprintAsync("fingerprint_keys", "fingerprint_id", "keep"));
    }

    [Fact]
    public async Task DeleteFingerprintsAsync_empty_is_a_noop()
    {
        var store = await NewStoreAsync();
        await SeedFingerprintAsync(store, "only");

        var deleted = await store.DeleteFingerprintsAsync(Array.Empty<string>());

        Assert.Equal(0, deleted);
        Assert.Equal(1, await store.GetFingerprintCountAsync());
    }

    // ============================================================
    // PruneAbsorbedObservationsAsync
    // ============================================================

    [Fact]
    public async Task PruneAbsorbedObservationsAsync_keeps_unabsorbed_and_newest_K_absorbed()
    {
        var store = await NewStoreAsync();
        await SeedFingerprintAsync(store, "fp");

        // 5 absorbed observations (older), then 2 unabsorbed (newest).
        for (var i = 0; i < 5; i++)
            await store.RecordObservationAsync(RequestScope.Unknown, "fp", UnitVector(), "chrome");
        await MarkAllObservationsAbsorbedAsync("fp");

        await store.RecordObservationAsync(RequestScope.Unknown, "fp", UnitVector(), "chrome");
        await store.RecordObservationAsync(RequestScope.Unknown, "fp", UnitVector(), "chrome");

        // Keep newest-2 absorbed + all unabsorbed.
        var pruned = await store.PruneAbsorbedObservationsAsync(keepPerFingerprint: 2);

        // 5 absorbed, keep newest 2 → prune 3.
        Assert.Equal(3, pruned);

        var total = await RowCountForFingerprintAsync("fingerprint_observations", "fingerprint_id", "fp");
        Assert.Equal(4, total); // 2 unabsorbed + 2 newest absorbed

        var unabsorbed = await UnabsorbedCountAsync("fp");
        Assert.Equal(2, unabsorbed);

        // The most-recent vector still resolves (drift reader relies on ORDER BY id DESC).
        Assert.NotNull(await store.GetLatestObservationVectorAsync("fp"));
    }

    [Fact]
    public async Task PruneAbsorbedObservationsAsync_never_prunes_unabsorbed()
    {
        var store = await NewStoreAsync();
        await SeedFingerprintAsync(store, "fp");

        for (var i = 0; i < 10; i++)
            await store.RecordObservationAsync(RequestScope.Unknown, "fp", UnitVector(), "chrome");
        // All unabsorbed.

        var pruned = await store.PruneAbsorbedObservationsAsync(keepPerFingerprint: 2);

        Assert.Equal(0, pruned); // nothing absorbed → nothing pruned
        Assert.Equal(10, await UnabsorbedCountAsync("fp"));
    }

    // ============================================================
    // ListAbsorbableObservationsAsync (working-set cap)
    // ============================================================

    [Fact]
    public async Task ListAbsorbableObservationsAsync_caps_to_most_recently_seen_fingerprints()
    {
        var store = await NewStoreAsync();
        var baseTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        // 5 fingerprints, each with one unabsorbed observation, at distinct last_seen
        // (fp-0 newest ... fp-4 oldest). RecordObservationAsync bumps last_seen to now,
        // so stamp the controlled value AFTER recording.
        for (var i = 0; i < 5; i++)
        {
            var id = $"fp-{i}";
            await SeedFingerprintAsync(store, id);
            await store.RecordObservationAsync(RequestScope.Unknown, id, UnitVector(), "chrome");
            await StampLastSeenAsync(id, baseTime.AddMinutes(-i));
        }

        // Cap to 3 => only the 3 most-recently-seen fingerprints' observations are absorbable
        // this run; colder ones age in over successive ticks.
        var capped = await store.ListAbsorbableObservationsAsync(
            maturityThreshold: 1, ageDays: 3650, activeWindowDays: 3650, maxFingerprints: 3);

        var cappedFps = capped.Select(o => o.FingerprintId).Distinct().OrderBy(x => x).ToList();
        Assert.Equal(new[] { "fp-0", "fp-1", "fp-2" }, cappedFps);
    }

    [Fact]
    public async Task ListAbsorbableObservationsAsync_nonpositive_cap_is_unbounded()
    {
        var store = await NewStoreAsync();
        for (var i = 0; i < 5; i++)
        {
            var id = $"fp-{i}";
            await SeedFingerprintAsync(store, id);
            await store.RecordObservationAsync(RequestScope.Unknown, id, UnitVector(), "chrome");
        }

        var all = await store.ListAbsorbableObservationsAsync(
            maturityThreshold: 1, ageDays: 3650, activeWindowDays: 3650, maxFingerprints: 0);

        Assert.Equal(5, all.Select(o => o.FingerprintId).Distinct().Count());
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

    private async Task SeedFingerprintAsync(
        SqliteFingerprintStore store,
        string id,
        double botProb = 0.1,
        string riskBand = "Low",
        string claimStatus = "unverified",
        DateTime? lastSeen = null)
    {
        var now = lastSeen ?? DateTime.UtcNow;
        var weights = new float[Dim];
        Array.Fill(weights, 1.0f);
        var fp = new Fingerprint
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
            ClaimStatus = claimStatus
        };
        await store.InsertFingerprintAsync(fp, $"sig-{id}");

        // Stamp the score-input columns directly: InsertFingerprintAsync derives the
        // band from the row and only persists it when a verdict timestamp exists, so
        // the test writes the deterministic values the priority scan reads.
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprints
               SET cached_bot_probability = @prob,
                   last_seen              = @seen,
                   claim_status           = @claim
             WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@prob", botProb);
        // No cached_risk_band column: the priority scan derives the band from the
        // probability at read (BucketRisk). riskBand is advisory only now.
        _ = riskBand;
        cmd.Parameters.AddWithValue("@seen", now.ToString("O"));
        cmd.Parameters.AddWithValue("@claim", claimStatus);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
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

    private async Task StampLastSeenAsync(string fingerprintId, DateTime lastSeen)
    {
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "UPDATE fingerprints SET last_seen = @seen WHERE fingerprint_id = @id";
        cmd.Parameters.AddWithValue("@seen", lastSeen.ToString("O"));
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<int> RowCountForFingerprintAsync(string table, string column, string fingerprintId)
    {
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT COUNT(*) FROM {table} WHERE {column} = @id";
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }

    private async Task<int> UnabsorbedCountAsync(string fingerprintId)
    {
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM fingerprint_observations
             WHERE fingerprint_id = @id AND absorbed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync());
    }
}
