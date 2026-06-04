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
///     Drives <see cref="FingerprintMatchContributor"/> through its six absorb
///     sites to assert step 3 of the composite-mode spec: the matcher reads the
///     <c>identity.browser_mode</c> signal, allocates a mode row when the
///     fingerprint hasn't shown that mode before, and EWMA-merges into the row
///     on every subsequent absorb. Parent fingerprint absorption is unaffected
///     so identity stability is preserved by construction.
/// </summary>
public sealed class MatcherBrowserModeAbsorbTests : IAsyncLifetime
{
    private string _tempDir = string.Empty;
    private SqliteFingerprintStore _store = null!;
    private SqliteFingerprintBrowserModeStore _modeStore = null!;
    private FingerprintModeAbsorptionService _drainer = null!;
    private IdentityProcessingCoordinator _coordinator = null!;
    private CancellationTokenSource _coordCts = null!;
    private FingerprintMatchContributor _matcher = null!;
    private IdentityVectorLayout _layout = null!;

    public async Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-mode-absorb-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions
            {
                Enabled = true,
                Engine = new IdentityEngineOptions { PreferSqliteVec = false },
                BrowserMode = new BrowserModeOptions { Enabled = true, FallbackModeId = "unknown" },
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
        _store = new SqliteFingerprintStore(NullLogger<SqliteFingerprintStore>.Instance, options, _layout);
        await _store.EnsureInitialisedAsync();

        var index = new BruteForceIdentityAnchorIndex(_store);
        var archetypes = new IdentityArchetypeRegistry(
            NullLogger<IdentityArchetypeRegistry>.Instance, new IdentityVectorEncoder(_layout));
        var globalWeights = new IdentityGlobalWeightsCache(
            NullLogger<IdentityGlobalWeightsCache>.Instance, _store, options);

        _coordinator = new IdentityProcessingCoordinator(
            NullLogger<IdentityProcessingCoordinator>.Instance, options);
        _coordCts = new CancellationTokenSource();
        await _coordinator.StartAsync(_coordCts.Token);

        _modeStore = new SqliteFingerprintBrowserModeStore(
            _store, options, NullLogger<SqliteFingerprintBrowserModeStore>.Instance);
        _drainer = new FingerprintModeAbsorptionService(
            NullLogger<FingerprintModeAbsorptionService>.Instance, _modeStore, options);
        var modes = new BrowserModeRegistry(
            NullLogger<BrowserModeRegistry>.Instance, fallbackModeId: "unknown");

        _matcher = new FingerprintMatchContributor(
            NullLogger<FingerprintMatchContributor>.Instance,
            _store, index, archetypes, globalWeights, _coordinator,
            new IdentityVectorEncoder(_layout), _modeStore, modes, options);
    }

    private Task<int> DrainAsync() =>
        _drainer.TickOnceAsync(maxRows: 5_000, CancellationToken.None);

    public async Task DisposeAsync()
    {
        _coordCts.Cancel();
        await _coordinator.StopAsync(CancellationToken.None);
        _coordinator.Dispose();
        _coordCts.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task NewFingerprint_RecordsObservation_DrainerSeedsModeRow()
    {
        var v = IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 41);
        var signals = await RunMatcherAsync(v, "sig-alloc-nav", browserMode: "navigation");

        var fpId = (string)signals[SignalKeys.IdentityFingerprintId];

        // Append-only: the matcher recorded an observation but the mode row
        // doesn't exist yet — it materialises only after the drainer ticks.
        var beforeDrain = await _modeStore.GetModesAsync(fpId);
        Assert.Empty(beforeDrain);

        // unseen=true on the request that introduced the mode (because the
        // cached mode row was null when the matcher emitted the signal).
        Assert.True(signals.TryGetValue(SignalKeys.IdentityBrowserModeUnseen, out var unseenObj));
        Assert.True((bool)unseenObj);
        Assert.Equal(0, (int)signals[SignalKeys.IdentityBrowserModeMaturity]);

        // One drain tick folds the observation into a fresh mode row.
        var folded = await DrainAsync();
        Assert.Equal(1, folded);

        var afterDrain = await _modeStore.GetModesAsync(fpId);
        Assert.Single(afterDrain);
        Assert.Equal("navigation", afterDrain[0].ModeId);
        Assert.Equal(1, afterDrain[0].CentroidMaturity);
        Assert.Equal(1, afterDrain[0].ObservationCount);
    }

    [Fact]
    public async Task L1Confirm_SameMode_BatchEWMAsAcrossAllPendingObservations()
    {
        var v = IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 43);

        // Three requests, same primarySig, same browser mode. Matcher records
        // three observations (no mode row yet). One drain tick folds them all
        // in a single batched EWMA against a freshly-seeded row.
        var s1 = await RunMatcherAsync(v, "sig-l1", browserMode: "navigation");
        var fpId = (string)s1[SignalKeys.IdentityFingerprintId];
        await RunMatcherAsync(v, "sig-l1", browserMode: "navigation");
        await RunMatcherAsync(v, "sig-l1", browserMode: "navigation");

        var folded = await DrainAsync();
        Assert.Equal(3, folded);

        var modes = await _modeStore.GetModesAsync(fpId);
        Assert.Single(modes);
        Assert.Equal("navigation", modes[0].ModeId);
        // Batched EWMA over 3 observations against an empty row = N=3.
        Assert.Equal(3, modes[0].CentroidMaturity);
        Assert.Equal(3, modes[0].ObservationCount);

        // After the drain, a fourth request sees the cached mode row's
        // maturity in the diagnostic signal — unseen=false, maturity=3
        // (pre-fold; the new observation lands in the next drain tick).
        var s4 = await RunMatcherAsync(v, "sig-l1", browserMode: "navigation");
        Assert.False(s4.TryGetValue(SignalKeys.IdentityBrowserModeUnseen, out _),
            "Cached mode row exists, so unseen must NOT be emitted.");
        Assert.Equal(3, (int)s4[SignalKeys.IdentityBrowserModeMaturity]);

        var folded2 = await DrainAsync();
        Assert.Equal(1, folded2);
        var afterSecondDrain = await _modeStore.GetModesAsync(fpId);
        Assert.Equal(4, afterSecondDrain.Single().CentroidMaturity);
        Assert.Equal(4, afterSecondDrain.Single().ObservationCount);
    }

    [Fact]
    public async Task L1Confirm_DifferentMode_DrainerSeedsEachTuple_Independently()
    {
        var v = IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 47);

        // Two requests in different modes against the same fingerprint. The
        // drainer groups by (fp, mode_id) and seeds each tuple independently.
        var s1 = await RunMatcherAsync(v, "sig-mix", browserMode: "navigation");
        var fpId = (string)s1[SignalKeys.IdentityFingerprintId];
        var s2 = await RunMatcherAsync(v, "sig-mix", browserMode: "xhr");

        // Second request's mode is also unseen at the time (no cached row).
        Assert.True(s2.TryGetValue(SignalKeys.IdentityBrowserModeUnseen, out var unseenObj));
        Assert.True((bool)unseenObj);

        await DrainAsync();

        var modes = await _modeStore.GetModesAsync(fpId);
        var byMode = modes.ToDictionary(m => m.ModeId, m => m);
        Assert.Equal(2, byMode.Count);
        Assert.Equal(1, byMode["navigation"].CentroidMaturity);
        Assert.Equal(1, byMode["xhr"].CentroidMaturity);
    }

    [Fact]
    public async Task ConcurrentAbsorbsForSameTuple_DoNotRace()
    {
        // The append-only + batched drain fix is exactly here: previously the
        // matcher did a read-modify-write per request, so N concurrent absorbs
        // for the same (fp, mode) tuple all read the same baseline and one
        // observation got lost when they raced to UPSERT. With the new design
        // each absorb is one INSERT; one drain tick folds them all.
        var v = IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 49);

        var first = await RunMatcherAsync(v, "sig-race", browserMode: "xhr");
        var fpId = (string)first[SignalKeys.IdentityFingerprintId];

        const int concurrentCount = 16;
        var tasks = new Task[concurrentCount];
        for (var i = 0; i < concurrentCount; i++)
            tasks[i] = RunMatcherAsync(v, "sig-race", browserMode: "xhr");
        await Task.WhenAll(tasks);

        var folded = await DrainAsync();
        Assert.Equal(concurrentCount + 1, folded);

        var modes = await _modeStore.GetModesAsync(fpId);
        Assert.Single(modes);
        Assert.Equal(concurrentCount + 1, modes[0].ObservationCount);
        Assert.Equal(concurrentCount + 1, modes[0].CentroidMaturity);
    }

    [Fact]
    public async Task ParentAbsorbPath_UnaffectedByModeAbsorb()
    {
        // Identity stability assertion: the parent store still receives
        // RecordObservationAsync per request, exactly as it did before step 3.
        var v = IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 53);
        var s1 = await RunMatcherAsync(v, "sig-parent", browserMode: "xhr");
        var fpId = (string)s1[SignalKeys.IdentityFingerprintId];
        await RunMatcherAsync(v, "sig-parent", browserMode: "xhr");
        await RunMatcherAsync(v, "sig-parent", browserMode: "xhr");

        var obs = await _store.GetUnabsorbedObservationCountAsync(fpId);
        // Allocate (no observation row); two L1 confirms each write one.
        Assert.Equal(2, obs);
    }

    private async Task<ConcurrentDictionary<string, object>> RunMatcherAsync(
        float[] vector, string primarySig, string browserMode)
    {
        var signals = new ConcurrentDictionary<string, object>
        {
            [SignalKeys.PrimarySignature] = primarySig,
            [SignalKeys.IdentityVector] = vector,
            [SignalKeys.IdentityBrowserMode] = browserMode,
        };
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
        await _matcher.ContributeAsync(state);
        return signals;
    }
}