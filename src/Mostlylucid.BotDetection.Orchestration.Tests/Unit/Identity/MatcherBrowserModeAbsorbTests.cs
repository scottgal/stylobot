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
        var modes = new BrowserModeRegistry(
            NullLogger<BrowserModeRegistry>.Instance, fallbackModeId: "unknown");

        _matcher = new FingerprintMatchContributor(
            NullLogger<FingerprintMatchContributor>.Instance,
            _store, index, archetypes, globalWeights, _coordinator,
            new IdentityVectorEncoder(_layout), _modeStore, modes, options);
    }

    public async Task DisposeAsync()
    {
        _coordCts.Cancel();
        await _coordinator.StopAsync(CancellationToken.None);
        _coordinator.Dispose();
        _coordCts.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task NewFingerprint_AllocatesModeRowSeededFromObservation()
    {
        var v = IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 41);
        var signals = await RunMatcherAsync(v, "sig-alloc-nav", browserMode: "navigation");

        var fpId = (string)signals[SignalKeys.IdentityFingerprintId];
        var modes = await _modeStore.GetModesAsync(fpId);
        Assert.Single(modes);
        Assert.Equal("navigation", modes[0].ModeId);
        Assert.Equal(1, modes[0].CentroidMaturity);
        Assert.Equal(1, modes[0].ObservationCount);

        // unseen=true on the request that introduced the mode.
        Assert.True(signals.TryGetValue(SignalKeys.IdentityBrowserModeUnseen, out var unseenObj));
        Assert.True((bool)unseenObj);

        Assert.Equal(1, (int)signals[SignalKeys.IdentityBrowserModeMaturity]);
    }

    [Fact]
    public async Task L1Confirm_SameMode_EWMAsCentroidAndIncrementsMaturity()
    {
        var v = IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 43);

        // Two requests, same primarySig, same browser mode -- second request hits
        // L1 confirm and absorbs into the existing mode row.
        var s1 = await RunMatcherAsync(v, "sig-l1", browserMode: "navigation");
        var fpId = (string)s1[SignalKeys.IdentityFingerprintId];
        var s2 = await RunMatcherAsync(v, "sig-l1", browserMode: "navigation");

        var modes = await _modeStore.GetModesAsync(fpId);
        Assert.Single(modes);
        Assert.Equal("navigation", modes[0].ModeId);
        Assert.Equal(2, modes[0].CentroidMaturity);
        Assert.Equal(2, modes[0].ObservationCount);

        // Second request: unseen=false, maturity=2.
        Assert.False(s2.TryGetValue(SignalKeys.IdentityBrowserModeUnseen, out _),
            "Second observation of the same mode must not emit identity.browser_mode_unseen.");
        Assert.Equal(2, (int)s2[SignalKeys.IdentityBrowserModeMaturity]);
    }

    [Fact]
    public async Task L1Confirm_DifferentMode_AllocatesNewModeRow_PreservesExisting()
    {
        var v = IdentityTestHelpers.MakeUnitVector(_layout.Dimension, seed: 47);

        // Request 1: navigation. Request 2: xhr against the same fingerprint
        // -- L1 confirms but the mode is unseen; a new row gets allocated and
        // the navigation row is untouched.
        var s1 = await RunMatcherAsync(v, "sig-mix", browserMode: "navigation");
        var fpId = (string)s1[SignalKeys.IdentityFingerprintId];
        var s2 = await RunMatcherAsync(v, "sig-mix", browserMode: "xhr");

        var modes = await _modeStore.GetModesAsync(fpId);
        var byMode = modes.ToDictionary(m => m.ModeId, m => m);
        Assert.Equal(2, byMode.Count);
        Assert.Equal(1, byMode["navigation"].CentroidMaturity);
        Assert.Equal(1, byMode["navigation"].ObservationCount);
        Assert.Equal(1, byMode["xhr"].CentroidMaturity);
        Assert.Equal(1, byMode["xhr"].ObservationCount);

        Assert.True(s2.TryGetValue(SignalKeys.IdentityBrowserModeUnseen, out var unseenObj),
            "Switching to a previously-unseen mode must emit identity.browser_mode_unseen.");
        Assert.True((bool)unseenObj);
    }

    [Fact]
    public async Task ParentAbsorbPath_UnaffectedByModeAbsorb()
    {
        // Identity stability assertion: with mode absorb wired in, the parent
        // store still receives RecordObservationAsync per request, just as it
        // did before step 3.
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