using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Test.Orchestration.Atoms.AtomContract;
using Mostlylucid.Ephemeral;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Surface-dimension drift is detected at the SESSION → FINGERPRINT ABSORPTION
///     BOUNDARY, not per request. The 7 raw surface dims (country / ASN / UA family /
///     datacenter+Tor introduction / canvas-WebGL shape hash / BotD verdict) ride the
///     single bounded per-fingerprint hot cache
///     (<see cref="SqliteFingerprintStore"/>'s <c>_fingerprintById</c> entry) as two
///     transient, never-persisted, co-evicted fields — <c>EstablishedDims</c> (baseline
///     shape) and <c>PendingDims</c> (latest-observed, stamped per request by
///     <see cref="Orchestration.Atoms.IdentityChangeAtom"/>). At absorption,
///     <see cref="FingerprintAbsorptionService"/> diffs pending vs established and, on a
///     change, persists a BOUNDED, DURABLE per-fingerprint drift summary: a fixed-width
///     7-float per-dim EWMA change-magnitude vector plus an EWMA change-frequency scalar.
///     <para>
///     The load-bearing containment test is
///     <see cref="StampObservedDims_nonResidentFingerprint_createsNoCacheEntry"/>: stamping
///     dims for a fingerprint that is not resident is a no-op, so a flood of rotated
///     fingerprints cannot grow an unbounded shadow accumulator (the #16 gateway OOM).
///     </para>
/// </summary>
public sealed class IdentityChangeDimFoldTests : IDisposable
{
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

    private IOptions<BotDetectionOptions> Options() => Microsoft.Extensions.Options.Options.Create(
        new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });

    private SqliteFingerprintStore NewStore()
        => new(NullLogger<SqliteFingerprintStore>.Instance, Options(), IdentityVectorLayout.DefaultV1());

    private FingerprintAbsorptionService NewService(SqliteFingerprintStore store)
    {
        var layout = IdentityVectorLayout.DefaultV1();
        var encoder = new IdentityVectorEncoder(layout);
        var archetypes = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, encoder);
        return new FingerprintAbsorptionService(
            NullLogger<FingerprintAbsorptionService>.Instance, store, archetypes, Options());
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
    // #3 Churn / no-unbounded-growth — THE #16 leak-containment guard.
    // ========================================================================

    [Fact]
    public async Task StampObservedDims_nonResidentFingerprint_createsNoCacheEntry()
    {
        var store = NewStore();
        await store.EnsureInitialisedAsync();

        // A flood of distinct, never-resident fingerprint ids — the exact rotation
        // pattern that leaked the old separate cache. Each stamp must be a no-op.
        for (var i = 0; i < 20_000; i++)
            store.StampObservedDims($"rotated-fp-{i}", Dims("US"));

        Assert.Equal(0, store.ResidentCacheCount);
    }

    // ========================================================================
    // #4 Established / pending — first stamp sets baseline silently, no drift.
    // ========================================================================

    [Fact]
    public async Task FirstAbsorption_establishesBaselineSilently_noDrift()
    {
        var store = NewStore();
        await SeedResidentAsync(store, "fp-baseline");
        var service = NewService(store);

        store.StampObservedDims("fp-baseline", Dims("US"));
        await service.DetectSurfaceDimDriftAsync("fp-baseline");

        var fp = await store.GetFingerprintAsync("fp-baseline");
        Assert.NotNull(fp);
        Assert.Equal(0.0, fp!.DriftFrequency);       // no drift on first sighting
        Assert.True(fp.DriftMagnitudes is null || fp.DriftMagnitudes.All(m => m == 0f));
    }

    // ========================================================================
    // #1 No-drift path — same dims across two cycles → frequency stays 0.
    // ========================================================================

    [Fact]
    public async Task StableDims_acrossTwoAbsorptions_recordsNoDrift()
    {
        var store = NewStore();
        await SeedResidentAsync(store, "fp-stable");
        var service = NewService(store);

        store.StampObservedDims("fp-stable", Dims("US"));
        await service.DetectSurfaceDimDriftAsync("fp-stable");   // baseline

        store.StampObservedDims("fp-stable", Dims("US"));        // same dims again
        await service.DetectSurfaceDimDriftAsync("fp-stable");   // compare

        var fp = await store.GetFingerprintAsync("fp-stable");
        Assert.NotNull(fp);
        Assert.Equal(0.0, fp!.DriftFrequency);
    }

    // ========================================================================
    // #2 Drift path — country change persists a bounded, durable summary.
    // ========================================================================

    [Fact]
    public async Task CountryChange_persistsBoundedDriftSummary_thatSurvivesRestart()
    {
        var store1 = NewStore();
        await SeedResidentAsync(store1, "fp-drift");
        var service = NewService(store1);

        // Cycle 1: establish baseline (US), no drift.
        store1.StampObservedDims("fp-drift", Dims("US"));
        await service.DetectSurfaceDimDriftAsync("fp-drift");

        // Cycle 2: country changed US -> DE → drift recorded.
        store1.StampObservedDims("fp-drift", Dims("DE"));
        await service.DetectSurfaceDimDriftAsync("fp-drift");

        var afterDrift = await store1.GetFingerprintAsync("fp-drift");
        Assert.NotNull(afterDrift);
        Assert.True(afterDrift!.DriftFrequency > 0.0, "change-frequency EWMA should be bumped");
        Assert.NotNull(afterDrift.DriftMagnitudes);
        Assert.Equal(SurfaceDims.DriftDimCount, afterDrift.DriftMagnitudes!.Length);
        Assert.True(afterDrift.DriftMagnitudes[SurfaceDims.CountryIndex] > 0f,
            "country slot magnitude should be non-zero");
        // Only the country dim changed — ASN/UA/etc. stay at 0.
        Assert.Equal(0f, afterDrift.DriftMagnitudes[SurfaceDims.AsnIndex]);

        // Durability: a fresh store instance over the SAME db file sees the summary.
        var store2 = NewStore();
        var reloaded = await store2.GetFingerprintAsync("fp-drift");
        Assert.NotNull(reloaded);
        Assert.True(reloaded!.DriftFrequency > 0.0, "drift frequency must survive restart");
        Assert.NotNull(reloaded.DriftMagnitudes);
        Assert.True(reloaded.DriftMagnitudes![SurfaceDims.CountryIndex] > 0f,
            "drift magnitudes must survive restart");
    }

    // ========================================================================
    // #5 LIVE per-request divergence — this request scores NOW when it differs
    // from the fingerprint's ESTABLISHED shape (compute-at-read, no side cache).
    // ========================================================================

    private const string Session = "session-1";

    private static SignalSink NewSink() => new(maxCapacity: 1000, maxAge: TimeSpan.FromMinutes(1));

    private static IdentityChangeAtom NewAtom(IFingerprintStore store) => new(
        NullLogger<IdentityChangeAtom>.Instance, new StubDetectorConfigProvider(), store);

    private static SignalSink SinkFor(string fingerprintId, string country)
    {
        var sink = NewSink();
        sink.Raise($"{SignalKeys.IdentityFingerprintId}:{fingerprintId}", Session);
        sink.Raise($"{SignalKeys.GeoCountryCode}:{country}", Session);
        return sink;
    }

    [Fact]
    public async Task LiveDivergence_currentDiffersFromEstablished_raisesRiskAndContribution()
    {
        var store = NewStore();
        await SeedResidentAsync(store, "fp-live");
        // Established baseline = US (as the absorption boundary would have promoted it).
        store.PromoteEstablishedDims("fp-live", Dims("US"));
        var atom = NewAtom(store);

        // This request presents DE → diverges from the established US shape → suspicious NOW.
        var sink = SinkFor("fp-live", "DE");
        var result = await atom.DetectAsync(sink, Session);

        Assert.Single(result);
        Assert.Equal("SurfaceDimShift", result[0].Category);
        Assert.Equal("US -> DE", sink.ReadHint(SignalKeys.RiskCountryTransition));
    }

    [Fact]
    public async Task LiveDivergence_noEstablishedBaseline_raisesNothing()
    {
        var store = NewStore();
        await SeedResidentAsync(store, "fp-nobaseline");
        var atom = NewAtom(store);

        // No established baseline promoted yet → nothing to diverge from; stamp only.
        var sink = SinkFor("fp-nobaseline", "DE");
        var result = await atom.DetectAsync(sink, Session);

        Assert.Empty(result);
        Assert.Null(sink.ReadHint(SignalKeys.RiskCountryTransition));
    }
}
