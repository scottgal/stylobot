using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Pins the contract for the persistent trust columns added in the
///     claim-verify-trust gap analysis (2026-06-15, Gap #4): the
///     <c>fingerprints</c> table gains <c>claim_status</c>,
///     <c>verification_method</c>, <c>verified_at</c>, <c>trust_observations</c>
///     and the <c>Fingerprint</c> record + <c>SqliteFingerprintStore</c>
///     round-trip them so a successful verification survives process restart.
///     <para>
///     Before this work trust was a one-way in-memory latch on
///     <c>SignatureCoordinator._isConfirmedFriendly</c> and vanished on
///     restart; tests here pin the durable shape so future regressions
///     (dropped columns, mis-indexed reader, missing migrations) fail loud.
///     </para>
/// </summary>
public class FingerprintTrustStateRoundTripTests : IDisposable
{
    private readonly string _tempDir;

    public FingerprintTrustStateRoundTripTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-trust-roundtrip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
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
        var layout = IdentityVectorLayout.DefaultV1();
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            options,
            layout);
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static Fingerprint NewFingerprint(
        string id,
        int dim,
        string claimStatus = "unverified",
        string? verificationMethod = null,
        DateTime? verifiedAt = null,
        int trustObservations = 0)
    {
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = id,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 1,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "chrome-desktop",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now,
            ClaimStatus = claimStatus,
            VerificationMethod = verificationMethod,
            VerifiedAt = verifiedAt,
            TrustObservations = trustObservations,
        };
    }

    /// <summary>
    ///     Schema check: the four trust columns exist on the live SQLite
    ///     <c>fingerprints</c> table after <c>EnsureInitialisedAsync</c> runs.
    ///     A regression that drops the migration or removes the column from
    ///     <c>identity_core.sql</c> fails here with a missing-column assertion
    ///     before any other test even runs.
    /// </summary>
    [Fact]
    public async Task Fingerprint_Schema_Includes_Trust_Columns()
    {
        // Force schema initialisation. Insert a row to ensure the WAL has
        // been committed-and-checkpointed onto the main file before the
        // PRAGMA read runs against a separate connection.
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        await store.InsertFingerprintAsync(
            NewFingerprint("schema-probe", dim), "sig-schema-probe");

        var fpDb = Path.Combine(_tempDir, "fingerprints.db");
        var listing = string.Join(", ", Directory.EnumerateFiles(_tempDir));
        Assert.True(File.Exists(fpDb),
            $"fingerprints.db missing at {fpDb}; tempdir contents: {listing}");

        await using var conn = new SqliteConnection($"Data Source={fpDb}");
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA table_info(fingerprints)";
        var cols = new List<string>();
        await using var rdr = await cmd.ExecuteReaderAsync();
        while (await rdr.ReadAsync())
            cols.Add(rdr.GetString(1));

        Assert.NotEmpty(cols);
        Assert.Contains("claim_status", cols);
        Assert.Contains("verification_method", cols);
        Assert.Contains("verified_at", cols);
        Assert.Contains("trust_observations", cols);
    }

    /// <summary>
    ///     INSERT-then-SELECT round-trip: a verified fingerprint persists
    ///     <c>claim_status='verified'</c>, the <c>verification_method</c>,
    ///     <c>verified_at</c>, and <c>trust_observations</c>, and reads them
    ///     back through <c>GetFingerprintAsync</c> unchanged.
    /// </summary>
    [Fact]
    public async Task Fingerprint_With_Trust_Columns_Round_Trips()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var verifiedAt = new DateTime(2026, 6, 15, 12, 34, 56, DateTimeKind.Utc);
        var fp = NewFingerprint(
            id: "fp-verified-001",
            dim: dim,
            claimStatus: "verified",
            verificationMethod: "fcrdns",
            verifiedAt: verifiedAt,
            trustObservations: 5);

        await store.InsertFingerprintAsync(fp, "sig-verified-001");

        // GetFingerprintAsync caches the row in the LFU dict on first read; the
        // L0 cache may return the synthesised post-insert row rather than the
        // canonical SELECT. Open a fresh store on the same db file to force a
        // cold read through ReadFingerprint.
        var coldStore = await NewStoreAsync();
        var loaded = await coldStore.GetFingerprintAsync(fp.FingerprintId);

        Assert.NotNull(loaded);
        Assert.Equal("verified", loaded!.ClaimStatus);
        Assert.Equal("fcrdns", loaded.VerificationMethod);
        Assert.NotNull(loaded.VerifiedAt);
        Assert.Equal(verifiedAt, loaded.VerifiedAt!.Value.ToUniversalTime());
        Assert.Equal(5, loaded.TrustObservations);
    }

    /// <summary>
    ///     A freshly-inserted fingerprint that did not opt into trust state
    ///     reads back as <c>'unverified'</c> with NULL method / verified_at /
    ///     zero trust observations. Pins the default-value contract so the
    ///     ALTER TABLE migration's column defaults match the SqliteFingerprintStore
    ///     INSERT defaults.
    /// </summary>
    [Fact]
    public async Task Fingerprint_Without_Trust_Defaults_To_Unverified()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var fp = NewFingerprint(id: "fp-unverified-001", dim: dim);

        await store.InsertFingerprintAsync(fp, "sig-unverified-001");

        var coldStore = await NewStoreAsync();
        var loaded = await coldStore.GetFingerprintAsync(fp.FingerprintId);

        Assert.NotNull(loaded);
        Assert.Equal("unverified", loaded!.ClaimStatus);
        Assert.Null(loaded.VerificationMethod);
        Assert.Null(loaded.VerifiedAt);
        Assert.Equal(0, loaded.TrustObservations);
    }

    /// <summary>
    ///     <c>UpdateClaimVerificationAsync</c> flips an existing fingerprint
    ///     row from <c>unverified</c> to <c>verified</c> and persists the
    ///     verification method + timestamp. Verifies the write-behind
    ///     LFU façade pattern: dict cache is updated in-place, so an
    ///     immediate re-read sees the new state, and the SQLite UPDATE
    ///     persists for restart-survival.
    /// </summary>
    [Fact]
    public async Task UpdateClaimVerification_Persists_And_Updates_LFU_Cache()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var fp = NewFingerprint(id: "fp-flip-001", dim: dim);
        await store.InsertFingerprintAsync(fp, "sig-flip-001");

        // Warm the LFU dict.
        _ = await store.GetFingerprintAsync(fp.FingerprintId);

        var verifiedAt = new DateTime(2026, 6, 15, 8, 0, 0, DateTimeKind.Utc);
        await store.UpdateClaimVerificationAsync(
            fp.FingerprintId,
            claimStatus: "verified",
            verificationMethod: "nodeinfo",
            verifiedAt: verifiedAt);

        // Same store -- the dict should be authoritative; this read must
        // reflect the new state without re-querying SQLite.
        var hot = await store.GetFingerprintAsync(fp.FingerprintId);
        Assert.NotNull(hot);
        Assert.Equal("verified", hot!.ClaimStatus);
        Assert.Equal("nodeinfo", hot.VerificationMethod);
        Assert.Equal(verifiedAt, hot.VerifiedAt!.Value.ToUniversalTime());

        // Cold store -- proves the UPDATE actually committed to disk.
        var coldStore = await NewStoreAsync();
        var cold = await coldStore.GetFingerprintAsync(fp.FingerprintId);
        Assert.NotNull(cold);
        Assert.Equal("verified", cold!.ClaimStatus);
        Assert.Equal("nodeinfo", cold.VerificationMethod);
        Assert.Equal(verifiedAt, cold.VerifiedAt!.Value.ToUniversalTime());
    }

    /// <summary>
    ///     <c>UpdateClaimVerificationAsync</c> records the <c>spoofed</c>
    ///     verdict with null verified_at, so a subsequent within-TTL trust
    ///     read does NOT short-circuit. This is the negative-decision write
    ///     path the verifier contributors use when rDNS / NodeInfo refutes
    ///     the UA claim.
    /// </summary>
    [Fact]
    public async Task UpdateClaimVerification_Records_Spoofed_With_Null_VerifiedAt()
    {
        var store = await NewStoreAsync();
        var dim = IdentityVectorLayout.DefaultV1().Dimension;
        var fp = NewFingerprint(id: "fp-spoofed-001", dim: dim);
        await store.InsertFingerprintAsync(fp, "sig-spoofed-001");

        await store.UpdateClaimVerificationAsync(
            fp.FingerprintId,
            claimStatus: "spoofed",
            verificationMethod: "fcrdns",
            verifiedAt: null);

        var coldStore = await NewStoreAsync();
        var loaded = await coldStore.GetFingerprintAsync(fp.FingerprintId);

        Assert.NotNull(loaded);
        Assert.Equal("spoofed", loaded!.ClaimStatus);
        Assert.Equal("fcrdns", loaded.VerificationMethod);
        Assert.Null(loaded.VerifiedAt);
    }

    /// <summary>
    ///     The default for <c>TrustOptions.TrustCacheTtl</c> is 24 hours -- the
    ///     verifier contributors short-circuit re-verification when a
    ///     fingerprint's <c>verified_at</c> is within this window. Pins the
    ///     default so a follow-on options-binding change cannot silently
    ///     widen / narrow the trust window without test failure.
    /// </summary>
    [Fact]
    public void TrustOptions_Default_TrustCacheTtl_Is_24Hours()
    {
        var opts = new TrustOptions();
        Assert.Equal(TimeSpan.FromHours(24), opts.TrustCacheTtl);
        Assert.True(opts.ShortCircuitOnCachedTrust);
    }
}