using System.Collections.Concurrent;
using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Tests for <see cref="ContentSequenceContributor" />.
///     Covers: metadata properties, document detection, sequence tracking,
///     divergence, cache-warm, prefetch, SignalR-expected guard.
/// </summary>
public class ContentSequenceContributorTests : IDisposable
{
    #region Infrastructure

    private sealed class StubConfigProvider : IDetectorConfigProvider
    {
        private readonly Dictionary<string, object> _parameters;

        public StubConfigProvider(Dictionary<string, object>? parameters = null)
            => _parameters = parameters ?? new Dictionary<string, object>();

        public DetectorManifest? GetManifest(string detectorName) => null;

        public DetectorDefaults GetDefaults(string detectorName) => new()
        {
            Weights = new WeightDefaults { Base = 1.0, BotSignal = 1.0, HumanSignal = 1.0, Verified = 1.0 },
            Confidence = new ConfidenceDefaults
                { BotDetected = 0.3, HumanIndicated = -0.2, Neutral = 0.0, StrongSignal = 0.5 },
            Parameters = new Dictionary<string, object>(_parameters)
        };

        public T GetParameter<T>(string detectorName, string parameterName, T defaultValue)
        {
            if (_parameters.TryGetValue(parameterName, out var val))
            {
                try { return (T)Convert.ChangeType(val, typeof(T)); }
                catch { return defaultValue; }
            }
            return defaultValue;
        }

        public Task<T> GetParameterAsync<T>(
            string detectorName, string parameterName, ConfigResolutionContext context,
            T defaultValue, CancellationToken ct = default)
            => Task.FromResult(GetParameter(detectorName, parameterName, defaultValue));

        public void InvalidateCache(string? detectorName = null) { }

        public IReadOnlyDictionary<string, DetectorManifest> GetAllManifests()
            => new Dictionary<string, DetectorManifest>();
    }

    private readonly SequenceContextStore _contextStore = new();
    private readonly CentroidSequenceStore _centroidStore;

    public ContentSequenceContributorTests()
    {
        _centroidStore = new CentroidSequenceStore(
            "Data Source=:memory:",
            NullLogger<CentroidSequenceStore>.Instance);
        // Do not call InitializeAsync — we only need GlobalChain for these tests.
    }

    public void Dispose() => _contextStore.Dispose();

    private ContentSequenceContributor CreateContributor(
        Dictionary<string, object>? configParams = null,
        BotClusterService? clusterService = null)
        => new(
            NullLogger<ContentSequenceContributor>.Instance,
            new StubConfigProvider(configParams),
            _contextStore,
            _centroidStore,
            new EndpointDivergenceTracker(),
            assetHashStore: null,
            clusterService);

    private static BlackboardState CreateState(
        string? signature = "test-sig",
        Action<HttpContext>? configureHttp = null,
        Dictionary<string, object>? extraSignals = null)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Method = "GET";
        ctx.Request.Path = "/";
        ctx.Request.Headers.UserAgent = "Mozilla/5.0";
        configureHttp?.Invoke(ctx);

        var signals = new ConcurrentDictionary<string, object>(extraSignals ?? new Dictionary<string, object>());
        if (signature is not null)
            signals[SignalKeys.PrimarySignature] = signature;

        return new BlackboardState
        {
            HttpContext = ctx,
            Signals = signals,
            SignalWriter = signals,
            CurrentRiskScore = 0,
            CompletedDetectors = ImmutableHashSet<string>.Empty,
            FailedDetectors = ImmutableHashSet<string>.Empty,
            Contributions = ImmutableList<DetectionContribution>.Empty,
            RequestId = Guid.NewGuid().ToString("N"),
            Elapsed = TimeSpan.Zero
        };
    }

    /// <summary>
    ///     Simulate a completed prior document request by populating the SequenceContextStore
    ///     so the contributor sees an existing context with an established chain.
    /// </summary>
    private void SeedDocumentContext(
        string signature,
        DateTimeOffset? lastRequest = null,
        CentroidType centroidType = CentroidType.Unknown)
    {
        // First do a real document request to create a properly seeded context
        var contributor = CreateContributor();
        var state = CreateState(
            signature: signature,
            configureHttp: ctx => ctx.Request.Headers["Sec-Fetch-Mode"] = "navigate");
        contributor.ContributeAsync(state, CancellationToken.None).GetAwaiter().GetResult();

        // Now update the LastRequest timestamp if requested
        if (lastRequest.HasValue)
        {
            var ctx = _contextStore.TryGet(signature);
            if (ctx != null)
                _contextStore.Update(signature, ctx with { LastRequest = lastRequest.Value });
        }
    }

    #endregion

    #region Metadata Properties

    [Fact]
    public void Name_ReturnsContentSequence()
    {
        var contributor = CreateContributor();
        Assert.Equal("ContentSequence", contributor.Name);
    }

    [Fact]
    public void Priority_ReturnsFour()
    {
        var contributor = CreateContributor();
        Assert.Equal(4, contributor.Priority);
    }

    [Fact]
    public void TriggerConditions_IsEmpty()
    {
        var contributor = CreateContributor();
        Assert.Empty(contributor.TriggerConditions);
    }

    #endregion

    #region No Signature

    [Fact]
    public async Task NoSignature_WritesNoSequenceSignals()
    {
        var contributor = CreateContributor();
        var state = CreateState(
            signature: null,
            configureHttp: ctx => ctx.Request.Headers["Sec-Fetch-Mode"] = "navigate");

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.Empty(result);
        Assert.False(state.Signals.ContainsKey(SignalKeys.SequencePosition),
            "No signature → must not write sequence.position");
        Assert.False(state.Signals.ContainsKey(SignalKeys.SequenceChainId),
            "No signature → must not write sequence.chain_id");
    }

    #endregion

    #region Document Request (Position 0)

    [Fact]
    public async Task DocumentRequest_SecFetchModeNavigate_WritesPositionZero()
    {
        var contributor = CreateContributor();
        const string sig = "doc-sig-position0";

        var state = CreateState(
            signature: sig,
            configureHttp: ctx => ctx.Request.Headers["Sec-Fetch-Mode"] = "navigate");

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequencePosition));
        var position = state.GetSignal<int>(SignalKeys.SequencePosition);
        Assert.Equal(0, position);
    }

    [Fact]
    public async Task DocumentRequest_SecFetchModeNavigate_WritesOnTrackTrue()
    {
        var contributor = CreateContributor();
        const string sig = "doc-sig-ontrack";

        var state = CreateState(
            signature: sig,
            configureHttp: ctx => ctx.Request.Headers["Sec-Fetch-Mode"] = "navigate");

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequenceOnTrack));
        var onTrack = state.GetSignal<bool>(SignalKeys.SequenceOnTrack);
        Assert.True(onTrack);
    }

    [Fact]
    public async Task DocumentRequest_SecFetchModeNavigate_WritesNonEmptyChainId()
    {
        var contributor = CreateContributor();
        const string sig = "doc-sig-chainid";

        var state = CreateState(
            signature: sig,
            configureHttp: ctx => ctx.Request.Headers["Sec-Fetch-Mode"] = "navigate");

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequenceChainId));
        var chainId = state.GetSignal<string>(SignalKeys.SequenceChainId);
        Assert.False(string.IsNullOrEmpty(chainId), "sequence.chain_id must be non-null and non-empty");
    }

    [Fact]
    public async Task DocumentRequest_AcceptHtmlGet_WritesPositionZero()
    {
        // Fallback detection: no Sec-Fetch-Mode, but Accept: text/html + GET
        var contributor = CreateContributor();
        const string sig = "doc-sig-accept-html";

        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Method = "GET";
                ctx.Request.Headers.Accept = "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8";
                // No Sec-Fetch-Mode
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequencePosition));
        Assert.Equal(0, state.GetSignal<int>(SignalKeys.SequencePosition));
    }

    #endregion

    #region Non-Document First Request (no active sequence)

    [Fact]
    public async Task NonDocumentFirstRequest_WritesNoSequenceSignals()
    {
        // API call as the very first request for this signature — no chain established yet
        var contributor = CreateContributor();
        const string sig = "sig-api-first";

        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Method = "GET";
                ctx.Request.Path = "/api/data";
                ctx.Request.Headers["Sec-Fetch-Mode"] = "cors";
                ctx.Request.Headers["Content-Type"] = "application/json";
            });

        var result = await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.Empty(result);
        Assert.False(state.Signals.ContainsKey(SignalKeys.SequencePosition),
            "Non-document first request must not write sequence.position");
        Assert.False(state.Signals.ContainsKey(SignalKeys.SequenceChainId),
            "Non-document first request must not write sequence.chain_id");
    }

    #endregion

    #region Continuation Request (Position 1+)

    [Fact]
    public async Task ContinuationRequest_AfterDocument_IncrementsPositionToOne()
    {
        const string sig = "sig-continuation";
        // Seed a completed document request so there's an established chain
        SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddMilliseconds(-300));

        var contributor = CreateContributor();
        // Send a static asset (script) request — expected in critical window
        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Path = "/assets/app.js";
                ctx.Request.Headers["Sec-Fetch-Dest"] = "script";
                ctx.Request.Headers["Sec-Fetch-Mode"] = "no-cors";
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequencePosition));
        var position = state.GetSignal<int>(SignalKeys.SequencePosition);
        Assert.Equal(1, position);
    }

    #endregion

    #region Machine-Speed Divergence

    [Fact]
    public async Task MachineSpeedRequest_SubTwentyMs_WritesDivergedTrue()
    {
        const string sig = "sig-machine-speed";
        // Seed context first, then stamp LastRequest immediately before ContributeAsync
        // to keep the gap well within the 20ms machine-speed threshold on slow CI.
        SeedDocumentContext(sig);

        var contributor = CreateContributor();
        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Path = "/api/data";
                ctx.Request.Headers["Sec-Fetch-Mode"] = "cors";
            });

        // Stamp right before the call — minimises elapsed time between seed and detect.
        var existing = _contextStore.TryGet(sig);
        if (existing != null)
            _contextStore.Update(sig, existing with { LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-2) });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequenceDiverged),
            "Machine-speed request must write sequence.diverged");
        var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
        Assert.True(diverged, "sequence.diverged must be true for sub-20ms inter-request gap");
    }

    #endregion

    #region Cache-Warm Detection

    [Fact]
    public async Task CacheWarm_NoStaticAssetsInCriticalWindow_WritesCacheWarmTrue_NotDiverged()
    {
        const string sig = "sig-cache-warm";
        // Seed with window start 600ms ago (past the critical 500ms window)
        // so phaseIndex will be > 0 when we send next request
        SeedDocumentContext(sig);
        // Manually update the window start to be 600ms ago
        var ctx = _contextStore.TryGet(sig)!;
        _contextStore.Update(sig, ctx with
        {
            WindowStartTime = DateTimeOffset.UtcNow.AddMilliseconds(-600),
            LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-400),
            ObservedStateSet = [] // No StaticAsset observed yet
        });

        var contributor = CreateContributor();
        // Send an API call in the mid-window (no static assets have appeared)
        var state = CreateState(
            signature: sig,
            configureHttp: ctx2 =>
            {
                ctx2.Request.Path = "/api/data";
                ctx2.Request.Headers["Sec-Fetch-Mode"] = "cors";
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequenceCacheWarm),
            "No static assets in critical window must write sequence.cache_warm");
        var cacheWarm = state.GetSignal<bool>(SignalKeys.SequenceCacheWarm);
        Assert.True(cacheWarm, "sequence.cache_warm must be true when no StaticAsset appeared in critical window");

        // Cache-warm with ApiCall should not cause divergence (cache-warm exception applies)
        var diverged = state.Signals.TryGetValue(SignalKeys.SequenceDiverged, out var divVal)
            && divVal is true;
        Assert.False(diverged, "Cache-warm ApiCall in mid-window must NOT be flagged as diverged");
    }

    #endregion

    #region Prefetch Detection

    [Fact]
    public async Task PrefetchRequest_PurposeHeader_WritesPrefetchDetected_NotDiverged()
    {
        const string sig = "sig-prefetch";
        SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddMilliseconds(-300));

        var contributor = CreateContributor();
        // Use Purpose: prefetch + Sec-Fetch-Mode: no-cors so it's NOT treated as a document request
        // (IsDocumentRequest requires "navigate" mode or Accept: text/html)
        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Path = "/next-page.html";
                ctx.Request.Headers["Purpose"] = "prefetch";
                ctx.Request.Headers["Sec-Fetch-Mode"] = "no-cors";
                ctx.Request.Headers["Sec-Fetch-Dest"] = "document";
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequencePrefetchDetected),
            "Prefetch request must write sequence.prefetch_detected");
        var prefetch = state.GetSignal<bool>(SignalKeys.SequencePrefetchDetected);
        Assert.True(prefetch);

        // Prefetch requests skip divergence scoring — sequence.diverged should be false
        Assert.True(state.Signals.ContainsKey(SignalKeys.SequenceDiverged));
        var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
        Assert.False(diverged, "Prefetch request must NOT be flagged as diverged");
    }

    #endregion

    #region HTTP/2 Parallel Static Assets — Not Diverged

    [Fact]
    public async Task StaticAssetInCriticalWindow_IsExpected_NotDiverged()
    {
        const string sig = "sig-static-asset";
        // Seed with a recent document request (300ms ago) — we're in the critical window
        SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddMilliseconds(-300));

        var contributor = CreateContributor();
        // Static asset in critical window: expected per PhaseExpectedSets[0]
        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Path = "/assets/style.css";
                ctx.Request.Headers["Sec-Fetch-Dest"] = "style";
                ctx.Request.Headers["Sec-Fetch-Mode"] = "no-cors";
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequenceDiverged));
        var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
        Assert.False(diverged, "StaticAsset in critical window is expected and must NOT cause divergence");
    }

    #endregion

    #region SignalR Expected — Not Written When Centroid Is Bot

    [Fact]
    public async Task SignalRExpected_CentroidIsBot_NotWritten()
    {
        const string sig = "sig-bot-centroid";
        // First do a document request to create the context
        SeedDocumentContext(sig);

        // Override the context to make it appear to be a Bot centroid
        var existingCtx = _contextStore.TryGet(sig)!;
        _contextStore.Update(sig, existingCtx with
        {
            CentroidType = CentroidType.Bot,
            // Set the expected chain so the next step would be SignalR
            ExpectedChain = [RequestState.SignalR],
            LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-500)
        });

        var contributor = CreateContributor();
        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Path = "/hub/signalr";
                ctx.Request.Headers["Sec-Fetch-Mode"] = "websocket";
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.False(state.Signals.ContainsKey(SignalKeys.SequenceSignalRExpected),
            "sequence.signalr_expected must NOT be written when centroid type is Bot");
    }

    [Fact]
    public async Task SignalRExpected_CentroidIsHuman_WithSignalRChain_Written()
    {
        const string sig = "sig-human-centroid-signalr";
        SeedDocumentContext(sig);

        // Override the context to be a Human centroid with SignalR as next expected state
        var existingCtx = _contextStore.TryGet(sig)!;
        _contextStore.Update(sig, existingCtx with
        {
            CentroidType = CentroidType.Human,
            ExpectedChain = [RequestState.StaticAsset, RequestState.SignalR],
            Position = 0,
            LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-500)
        });

        var contributor = CreateContributor();
        // A request that would advance position to 1 (where SignalR is expected)
        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Path = "/assets/app.js";
                ctx.Request.Headers["Sec-Fetch-Dest"] = "script";
                ctx.Request.Headers["Sec-Fetch-Mode"] = "no-cors";
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        // At position 1, the chain has SignalR at index 1
        Assert.True(state.Signals.ContainsKey(SignalKeys.SequenceSignalRExpected),
            "sequence.signalr_expected must be written when centroid is Human and next chain step is SignalR");
        var signalRExpected = state.GetSignal<bool>(SignalKeys.SequenceSignalRExpected);
        Assert.True(signalRExpected);
    }

    #endregion
}
