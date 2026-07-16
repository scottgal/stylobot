using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     <see cref="FingerprintEvictionGuardian"/> (Part B / Task 10): cross-fingerprint
///     <see cref="Storage.MemoryAdaptiveCap"/> eviction by
///     <see cref="Storage.DecisionNecessity"/>, mirroring
///     <c>SignatureCapGuardian</c>. Cold-and-harmless fingerprints go first;
///     uncertain (near <c>BotFloor</c>) and risky ones survive; <c>verified</c>
///     claims are NEVER evicted even when cold.
/// </summary>
public class FingerprintEvictionGuardianTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _fpDb;
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    public FingerprintEvictionGuardianTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fp-eviction-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _fpDb = Path.Combine(_tempDir, "fingerprints.db");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private BotDetectionOptions Options(int max, int min) => new()
    {
        DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
        Identity = new IdentityOptions
        {
            Enabled = true,
            MaxFingerprints = max,
            MinFingerprints = min
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

    private static FingerprintEvictionGuardian NewGuardian(
        SqliteFingerprintStore store, BotDetectionOptions opts)
        => new(
            store,
            Microsoft.Extensions.Options.Options.Create(opts),
            new ConfigurationBuilder().AddInMemoryCollection().Build(),
            NullLogger<FingerprintEvictionGuardian>.Instance);

    [Fact]
    public async Task GuardAsync_evicts_cold_harmless_keeps_uncertain_and_risky()
    {
        var opts = Options(max: 2, min: 1);
        var store = await NewStoreAsync(opts);
        var now = DateTime.UtcNow;

        // Two cold-and-harmless (low botprob, old, low risk) -> evicted first.
        await SeedFingerprintAsync(store, "cold-1", botProb: 0.03, riskBand: "VeryLow", lastSeen: now.AddDays(-60));
        await SeedFingerprintAsync(store, "cold-2", botProb: 0.05, riskBand: "Low", lastSeen: now.AddDays(-45));
        // Uncertain: sits on the decision line (BotFloor 0.70) -> max uncertainty -> keep.
        await SeedFingerprintAsync(store, "uncertain", botProb: 0.70, riskBand: "Medium", lastSeen: now);
        // Risky: high risk band regardless of certainty -> keep.
        await SeedFingerprintAsync(store, "risky", botProb: 0.98, riskBand: "VeryHigh", lastSeen: now);

        var report = await NewGuardian(store, opts).GuardAsync();

        Assert.Equal("evicted", report.Status);
        Assert.Equal(2, await store.GetFingerprintCountAsync()); // back to <= cap

        Assert.False(await ExistsAsync("cold-1"));
        Assert.False(await ExistsAsync("cold-2"));
        Assert.True(await ExistsAsync("uncertain"));
        Assert.True(await ExistsAsync("risky"));
    }

    [Fact]
    public async Task GuardAsync_never_evicts_verified_even_when_cold()
    {
        var opts = Options(max: 1, min: 1);
        var store = await NewStoreAsync(opts);
        var now = DateTime.UtcNow;

        // A verified fingerprint that is otherwise the coldest thing in the store.
        await SeedFingerprintAsync(
            store, "verified-cold", botProb: 0.01, riskBand: "VeryLow",
            claimStatus: "verified", lastSeen: now.AddDays(-90));
        // Two evictable cold non-verified fingerprints.
        await SeedFingerprintAsync(store, "cold-a", botProb: 0.04, riskBand: "Low", lastSeen: now.AddDays(-30));
        await SeedFingerprintAsync(store, "cold-b", botProb: 0.06, riskBand: "Low", lastSeen: now.AddDays(-20));

        await NewGuardian(store, opts).GuardAsync();

        // cap=1, 3 present, 2 non-verified evictable -> both evicted, verified held.
        Assert.True(await ExistsAsync("verified-cold"));
        Assert.False(await ExistsAsync("cold-a"));
        Assert.False(await ExistsAsync("cold-b"));
    }

    [Fact]
    public async Task GuardAsync_within_cap_is_a_noop()
    {
        var opts = Options(max: 10, min: 1);
        var store = await NewStoreAsync(opts);
        await SeedFingerprintAsync(store, "a");
        await SeedFingerprintAsync(store, "b");

        var report = await NewGuardian(store, opts).GuardAsync();

        Assert.Equal("ok", report.Status);
        Assert.Equal(2, await store.GetFingerprintCountAsync());
    }

    [Fact]
    public async Task GuardAsync_holds_when_protected_alone_exceeds_cap()
    {
        var opts = Options(max: 1, min: 1);
        var store = await NewStoreAsync(opts);
        var now = DateTime.UtcNow;

        // Two verified fingerprints, cap 1: nothing evictable, so hold everything.
        await SeedFingerprintAsync(store, "v1", claimStatus: "verified", lastSeen: now.AddDays(-10));
        await SeedFingerprintAsync(store, "v2", claimStatus: "verified", lastSeen: now.AddDays(-20));

        await NewGuardian(store, opts).GuardAsync();

        Assert.True(await ExistsAsync("v1"));
        Assert.True(await ExistsAsync("v2"));
        Assert.Equal(2, await store.GetFingerprintCountAsync());
    }

    [Fact]
    public async Task Cap_disabled_when_MaxFingerprints_zero()
    {
        var opts = Options(max: 0, min: 1);
        var store = await NewStoreAsync(opts);
        await SeedFingerprintAsync(store, "a");
        await SeedFingerprintAsync(store, "b");
        await SeedFingerprintAsync(store, "c");

        var report = await NewGuardian(store, opts).GuardAsync();

        Assert.Equal("ok", report.Status);
        Assert.Equal(3, await store.GetFingerprintCountAsync());
    }

    [Fact]
    public void Guardian_identity_is_data_category()
    {
        var opts = Options(max: 10, min: 1);
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            Microsoft.Extensions.Options.Options.Create(opts),
            IdentityVectorLayout.DefaultV1());
        var guardian = NewGuardian(store, opts);

        Assert.Equal("FingerprintEviction", guardian.Name);
        Assert.Equal(GuardianCategory.Data, guardian.Category);
        Assert.True(guardian.Enabled);
    }

    // ============================================================
    // Helpers
    // ============================================================

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
            ClaimStatus = claimStatus
        }, $"sig-{id}");

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
        // No cached_risk_band column: the priority scan DERIVES the band from the
        // probability at read (BucketRisk). Seeded botProb/riskBand pairs are aligned, so
        // coldness ordering is unchanged; the riskBand arg is advisory only now.
        _ = riskBand;
        cmd.Parameters.AddWithValue("@seen", now.ToString("O"));
        cmd.Parameters.AddWithValue("@claim", claimStatus);
        cmd.Parameters.AddWithValue("@id", id);
        await cmd.ExecuteNonQueryAsync();
    }

    private async Task<bool> ExistsAsync(string fingerprintId)
    {
        await using var conn = new SqliteConnection($"Data Source={_fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fingerprints WHERE fingerprint_id = @id";
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync()) > 0;
    }
}
