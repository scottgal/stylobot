using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     #16 gateway-OOM fix: the per-fingerprint surface-dim drift lookback that
///     <see cref="IdentityChangeAtom"/> needs was folded OUT of the separate,
///     unbounded <c>FingerprintDimSnapshotCache</c> (the leak: one entry per rotated
///     fingerprint, never evicted) and INTO the single bounded
///     <see cref="SqliteFingerprintStore"/> per-fingerprint hot cache
///     (<c>_fingerprintById</c>), co-indexed in the same cache entry, co-evicted with
///     the fingerprint, and NEVER persisted to SQLite.
///     <para>
///     The load-bearing test is <see cref="SetLastSeenDims_nonResidentFingerprint_createsNoCacheEntry"/>:
///     it is the churn / no-unbounded-growth guard that proves the leak cannot recur —
///     dims can only ride on a resident fingerprint entry, so a flood of rotated
///     fingerprints cannot grow an independent accumulator.
///     </para>
/// </summary>
public sealed class IdentityChangeDimFoldTests : IDisposable
{
    private const string Session = "session-1";
    private static readonly int Dim = IdentityVectorLayout.DefaultV1().Dimension;

    private readonly string _tempDir;

    public IdentityChangeDimFoldTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"fp-dimfold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    private SqliteFingerprintStore NewStore()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        return new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance,
            options,
            IdentityVectorLayout.DefaultV1());
    }

    /// <summary>Insert a fingerprint row and make it RESIDENT in the in-memory cache
    /// (InsertFingerprintAsync deliberately does not populate the cache).</summary>
    private static async Task SeedResidentAsync(SqliteFingerprintStore store, string id)
    {
        var weights = new float[Dim];
        Array.Fill(weights, 1.0f);
        var now = DateTime.UtcNow;
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
            ClaimStatus = "unverified"
        };
        await store.InsertFingerprintAsync(fp, $"sig-{id}");
        _ = await store.GetFingerprintAsync(id); // populate _fingerprintById
    }

    private static SurfaceDims Dims(string country, string asn = "AS1", string uaFamily = "chrome")
        => new(country, asn, uaFamily, IsDatacenter: false, IsTorOrVpn: false,
               LastSeenUtc: DateTimeOffset.UtcNow);

    // ========================================================================
    // Churn / no-unbounded-growth — THE leak-regression guard.
    // ========================================================================

    [Fact]
    public async Task SetLastSeenDims_nonResidentFingerprint_createsNoCacheEntry()
    {
        var store = NewStore();
        await store.EnsureInitialisedAsync();

        // A flood of distinct, never-resident fingerprint ids — the exact rotation
        // pattern that leaked the old separate cache. Each Set must be a no-op.
        for (var i = 0; i < 20_000; i++)
            store.SetLastSeenDims($"rotated-fp-{i}", Dims("US"));

        Assert.Equal(0, store.ResidentCacheCount);
        Assert.Null(store.GetLastSeenDims("rotated-fp-0"));
        Assert.Null(store.GetLastSeenDims("rotated-fp-19999"));
    }

    // ========================================================================
    // Co-residency — dims live on the SAME bounded entry as the fingerprint.
    // ========================================================================

    [Fact]
    public async Task SetLastSeenDims_residentFingerprint_isReadableAndSharesTheOneEntry()
    {
        var store = NewStore();
        await SeedResidentAsync(store, "fp-a");
        Assert.Equal(1, store.ResidentCacheCount);

        store.SetLastSeenDims("fp-a", Dims("DE", asn: "AS42"));

        var read = store.GetLastSeenDims("fp-a");
        Assert.NotNull(read);
        Assert.Equal("DE", read!.Country);
        Assert.Equal("AS42", read.Asn);
        // No new entry — dims ride the existing fingerprint entry.
        Assert.Equal(1, store.ResidentCacheCount);
    }

    // ========================================================================
    // Non-persistence — dims are in-mem only; a fresh process sees none.
    // ========================================================================

    [Fact]
    public async Task LastSeenDims_areNeverPersisted_freshStoreInstanceSeesNull()
    {
        var store1 = NewStore();
        await SeedResidentAsync(store1, "fp-persist");
        store1.SetLastSeenDims("fp-persist", Dims("FR"));
        Assert.NotNull(store1.GetLastSeenDims("fp-persist"));

        // Fresh store over the SAME db file: the fingerprint row persisted, the dims did not.
        var store2 = NewStore();
        var fp = await store2.GetFingerprintAsync("fp-persist");
        Assert.NotNull(fp); // row survived
        Assert.Null(store2.GetLastSeenDims("fp-persist")); // dims did not
    }

    // ========================================================================
    // Drift detection preserved — through the atom, end to end.
    // ========================================================================

    private static SignalSink NewSink() => new(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));

    private static IdentityChangeAtom NewAtom(IFingerprintStore store) => new(
        NullLogger<IdentityChangeAtom>.Instance,
        new StubDetectorConfigProvider(),
        store);

    private static SignalSink SinkFor(string fingerprintId, string country)
    {
        var sink = NewSink();
        sink.Raise($"{SignalKeys.IdentityFingerprintId}:{fingerprintId}", Session);
        sink.Raise($"{SignalKeys.GeoCountryCode}:{country}", Session);
        return sink;
    }

    [Fact]
    public async Task IdentityChange_baselinesSilently_thenRaisesCountryDriftOnChange()
    {
        var store = NewStore();
        await SeedResidentAsync(store, "fp-drift");
        var atom = NewAtom(store);

        // Request 1: first sighting → baseline, no risk signal, no contribution.
        var sink1 = SinkFor("fp-drift", "US");
        var r1 = await atom.DetectAsync(sink1, Session);
        Assert.Empty(r1);
        Assert.Null(sink1.ReadHint(SignalKeys.RiskCountryTransition));

        // Request 2: same fingerprint, different country → drift raised.
        var sink2 = SinkFor("fp-drift", "DE");
        var r2 = await atom.DetectAsync(sink2, Session);
        Assert.Single(r2);
        Assert.Equal("SurfaceDimShift", r2[0].Category);
        Assert.Equal("US -> DE", sink2.ReadHint(SignalKeys.RiskCountryTransition));
    }
}
