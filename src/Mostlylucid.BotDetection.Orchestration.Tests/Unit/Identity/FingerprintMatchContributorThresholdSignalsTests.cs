using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Identity;

/// <summary>
///     Pins the three threshold-signal contracts introduced in Task 2 of the
///     identity async un-drift plan:
///
///     1. <see cref="SignalKeys.IdentityFingerprintFirstSeen"/> emitted on new-fp allocate.
///     2. <see cref="SignalKeys.IdentityFingerprintObservationCountCrossed"/> emitted when
///        the durable observation_count hits a configured crossing.
///     3. <see cref="SignalKeys.IdentityFingerprintMaturityCrossed"/> emitted when the
///        matched fingerprint's centroid_maturity exactly equals
///        <see cref="IdentityVectorOptions.AbsorptionMaturityThreshold"/>.
///
///     Harness shape matches <see cref="FingerprintMatcherConvergenceTests"/> (same project,
///     same infrastructure). The BDF rig (BdfReplayTests.Integration.cs, Task 1) covers the
///     production-policy path for <c>IdentityFingerprintFirstSeen</c>; these unit tests cover
///     the count-crossed and maturity-crossed contracts that the BDF rig does not assert.
///
///     Note: tests run against DetectionPolicy.Default implicitly because the matcher is
///     invoked directly (not through the orchestrator's policy layer). The BDF rig is the
///     authoritative production-policy gate for the first-seen signal.
/// </summary>
[Collection("IdentitySqlite")]
public sealed class FingerprintMatchContributorThresholdSignalsTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private SqliteFingerprintStore _store = null!;
    private IdentityProcessingCoordinator _coordinator = null!;
    private CancellationTokenSource _coordCts = null!;
    private FingerprintMatchContributor _matcher = null!;
    private IdentityVectorLayout _layout = null!;
    private IOptions<BotDetectionOptions> _options = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-fp-threshold-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        _options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false },
                Coordinator = new IdentityCoordinatorOptions
                {
                    WorkerCount = 1,
                    MaxQueueDepth = 32,
                    MaxQueuedPerFingerprint = 8,
                    CoalesceWindowMs = 1
                }
            }
        });

        _layout = IdentityVectorLayout.DefaultV1();
        _store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, _options, _layout);
        await _store.EnsureInitialisedAsync();

        _matcher = BuildMatcher(_options);
    }

    public async Task DisposeAsync()
    {
        _coordCts.Cancel();
        await _coordinator.StopAsync(CancellationToken.None);
        _coordinator.Dispose();
        _coordCts.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    // ---------------------------------------------------------------------------
    // Test 1: new-fp allocate path emits IdentityFingerprintFirstSeen = true
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task NewFingerprint_EmitsFirstSeenSignal()
    {
        var vector = MakeUnitVector(seed: 7);
        var signals = await RunMatcherAsync(vector, primarySig: "new-fp-sig-1");

        Assert.True(
            signals.TryGetValue(SignalKeys.IdentityFingerprintFirstSeen, out var val) && val is true,
            $"Expected {SignalKeys.IdentityFingerprintFirstSeen} = true on new-fp allocate path; " +
            $"got {(signals.TryGetValue(SignalKeys.IdentityFingerprintFirstSeen, out var raw) ? raw : "(absent)")}");
    }

    // ---------------------------------------------------------------------------
    // Test 2: observation count emits IdentityFingerprintObservationCountCrossed
    //         when the count hits a configured threshold
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task ObservationCount_EmitsCrossedSignal_OnConfiguredThresholds()
    {
        // Configure a single threshold at 3 so we can drive to it with deterministic
        // request count. Use a fresh matcher bound to the same store so the threshold
        // option is visible to EmitPostObservationSignalsAsync.
        var thresholdOptions = Options.Create(new BotDetectionOptions
        {
            DatabasePath = _options.Value.DatabasePath,
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false },
                Coordinator = _options.Value.Identity.Coordinator,
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = 5,
                    NotifyOnCountCrossings = new[] { 3 }
                }
            }
        });
        var matcher = BuildMatcher(thresholdOptions);

        var vector = MakeUnitVector(seed: 11);
        const string sig = "obs-threshold-sig-1";

        // Request 1: allocates the fingerprint. observation_count after InsertFingerprintAsync = 1.
        // No RecordObservationAsync is called on the allocate path (the row IS the first obs).
        var signals1 = await RunMatcherAsync(vector, sig, matcherOverride: matcher);

        // Requests 2 and 3: L1-confirm path calls RecordObservationAsync; count goes 2 then 3.
        var signals2 = await RunMatcherAsync(vector, sig, matcherOverride: matcher);
        var signals3 = await RunMatcherAsync(vector, sig, matcherOverride: matcher);

        // Signal must be absent on requests 1 and 2 (counts 1 and 2 don't hit threshold 3).
        Assert.False(
            signals1.TryGetValue(SignalKeys.IdentityFingerprintObservationCountCrossed, out _),
            $"Request 1 (new-fp, count=1) must not emit {SignalKeys.IdentityFingerprintObservationCountCrossed}");

        Assert.False(
            signals2.TryGetValue(SignalKeys.IdentityFingerprintObservationCountCrossed, out _),
            $"Request 2 (count=2) must not emit {SignalKeys.IdentityFingerprintObservationCountCrossed}");

        // Signal must be present on request 3 (count=3 = threshold).
        Assert.True(
            signals3.TryGetValue(SignalKeys.IdentityFingerprintObservationCountCrossed, out var crossedVal)
            && crossedVal is int n && n == 3,
            $"Request 3 (count=3) must emit {SignalKeys.IdentityFingerprintObservationCountCrossed} = 3; " +
            $"got {(signals3.TryGetValue(SignalKeys.IdentityFingerprintObservationCountCrossed, out var r2) ? r2 : "(absent)")}");
    }

    // ---------------------------------------------------------------------------
    // Test 3: maturity-crossed emitted when centroid_maturity == AbsorptionMaturityThreshold
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MaturityCrossing_EmitsMaturityCrossedSignal()
    {
        // Set threshold to 3 so we reach it quickly without dozens of requests.
        var maturityOptions = Options.Create(new BotDetectionOptions
        {
            DatabasePath = _options.Value.DatabasePath,
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false },
                Coordinator = _options.Value.Identity.Coordinator,
                Vector = new IdentityVectorOptions
                {
                    AbsorptionMaturityThreshold = 3,
                    NotifyOnCountCrossings = Array.Empty<int>()
                }
            }
        });
        var matcher = BuildMatcher(maturityOptions);

        var vector = MakeUnitVector(seed: 17);
        const string sig = "maturity-crossing-sig-1";

        // Seed a fingerprint manually at centroid_maturity = 2 (one below the threshold)
        // so the matcher's L1-confirm path sees a pre-loaded candidate whose maturity
        // is exactly threshold - 1 on request 1, and threshold on request 2.
        var dim = _layout.Dimension;
        var centroid = (float[])vector.Clone();
        var fpId = "maturity-fp-1";
        var fp2 = IdentityTestHelpers.MakeFingerprint(
            fpId, centroid,
            centroidMaturity: 2,
            quality: 0.9,
            cachedBotProbability: 0.0,
            cachedRiskBand: null,
            cachedScoreUpdatedAt: null,
            displayName: fpId);
        await _store.InsertFingerprintAsync(fp2, sig, CancellationToken.None);

        // Request 1: L1 confirm; matcher reads centroid_maturity = 2 (below threshold=3).
        var signals1 = await RunMatcherAsync(vector, sig, matcherOverride: matcher);

        // Manually absorb to bump centroid_maturity from 2 to 3 so the next L1 read
        // sees maturity == threshold. We use AbsorbObservationAsync to simulate the
        // absorption service tick without starting a real hosted service.
        // The fingerprint_observations row is not seeded here; the maturity bump happens
        // via the UpdateFingerprints fixture path keyed on fingerprint_id.
        await _store.AbsorbObservationAsync(
            observationId: 1,
            fingerprintId: fpId,
            newCentroid: centroid,
            newMaturity: 3,
            newWeights: fp2.Weights,
            ct: CancellationToken.None);

        // Request 2: L1 confirm again; matcher reads centroid_maturity = 3 = threshold.
        var signals2 = await RunMatcherAsync(vector, sig, matcherOverride: matcher);

        // Request 3: L1 confirm; matcher reads centroid_maturity = 3 still (absorption
        // service hasn't ticked again). Signal fires again (idempotent emission).
        var signals3 = await RunMatcherAsync(vector, sig, matcherOverride: matcher);

        // Request 1: maturity was 2, below threshold 3. No signal.
        Assert.False(
            signals1.TryGetValue(SignalKeys.IdentityFingerprintMaturityCrossed, out _),
            $"Request 1 (maturity=2 < threshold=3) must not emit {SignalKeys.IdentityFingerprintMaturityCrossed}");

        // Request 2: maturity read from the matched row was 3 == threshold. Signal fires.
        Assert.True(
            signals2.TryGetValue(SignalKeys.IdentityFingerprintMaturityCrossed, out var mVal) && mVal is true,
            $"Request 2 (maturity=3 == threshold=3) must emit {SignalKeys.IdentityFingerprintMaturityCrossed} = true; " +
            $"got {(signals2.TryGetValue(SignalKeys.IdentityFingerprintMaturityCrossed, out var r3) ? r3 : "(absent)")}");

        // Request 3: maturity still 3 == threshold; signal fires again (subscriber is responsible for idempotence).
        Assert.True(
            signals3.TryGetValue(SignalKeys.IdentityFingerprintMaturityCrossed, out var mVal3) && mVal3 is true,
            $"Request 3 (maturity=3 == threshold=3) must also emit {SignalKeys.IdentityFingerprintMaturityCrossed} = true");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    /// <summary>
    ///     Runs the matcher for one request and returns the resulting signal dictionary.
    ///     When <paramref name="matcherOverride"/> is supplied, uses it instead of the
    ///     fixture-level matcher; lets individual tests use a locally configured matcher
    ///     without rebuilding the entire fixture.
    /// </summary>
    private async Task<ConcurrentDictionary<string, object>> RunMatcherAsync(
        float[] vector,
        string primarySig,
        IReadOnlyDictionary<string, object>? extraSignals = null,
        FingerprintMatchContributor? matcherOverride = null)
    {
        var signals = new ConcurrentDictionary<string, object>();
        signals[SignalKeys.PrimarySignature] = primarySig;
        signals[SignalKeys.IdentityVector] = vector;
        if (extraSignals is not null)
            foreach (var kv in extraSignals)
                signals[kv.Key] = kv.Value;

        var state = new BlackboardState
        {
            HttpContext = new DefaultHttpContext(),
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = new HashSet<string>(),
            FailedDetectors = new HashSet<string>(),
            Contributions = Array.Empty<DetectionContribution>(),
            RequestId = Guid.NewGuid().ToString()
        };

        var m = matcherOverride ?? _matcher;
        await m.ContributeAsync(state);
        return signals;
    }

    private FingerprintMatchContributor BuildMatcher(IOptions<BotDetectionOptions> opts)
    {
        // Tear down previous coordinator if rebuilding mid-test.
        if (_coordinator is not null)
        {
            _coordCts.Cancel();
            _coordinator.StopAsync(CancellationToken.None).GetAwaiter().GetResult();
            _coordinator.Dispose();
            _coordCts.Dispose();
        }

        var index = new BruteForceIdentityAnchorIndex(_store);
        var archetypes = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance,
            new IdentityVectorEncoder(_layout));
        var globalWeights = new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance, _store, opts);

        _coordCts = new CancellationTokenSource();
        _coordinator = new IdentityProcessingCoordinator(
            NullLogger<IdentityProcessingCoordinator>.Instance, opts);
        _coordinator.StartAsync(_coordCts.Token).GetAwaiter().GetResult();

        var modeStore = new SqliteFingerprintBrowserModeStore(
            _store, opts, NullLogger<SqliteFingerprintBrowserModeStore>.Instance);
        var modes = new BrowserModeRegistry(
            NullLogger<BrowserModeRegistry>.Instance,
            fallbackModeId: "unknown");
        var modeResolver = new CachingBrowserModeResolver(modes);

        return new FingerprintMatchContributor(
            NullLogger<FingerprintMatchContributor>.Instance,
            _store,
            index,
            archetypes,
            globalWeights,
            _coordinator,
            new IdentityVectorEncoder(_layout),
            modeStore,
            modeResolver,
            opts);
    }

    private float[] MakeUnitVector(int seed) =>
        IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed);
}
