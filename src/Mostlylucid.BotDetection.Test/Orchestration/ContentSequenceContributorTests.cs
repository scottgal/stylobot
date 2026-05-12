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
    public async Task MachineSpeedPlusNotFound_TripsDivergence()
    {
        // Verifies: machine-speed timing (sub-20ms) combined with an unexpected NotFound
        // classification (404 response, weight 0.50) crosses the 0.6 threshold.
        // Under the new per-state weighting, machine-speed alone (0.3) is intentionally
        // insufficient: see MachineSpeedAloneOnExpectedState_DoesNotTrip below.
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
                ctx.Response.StatusCode = 404;
            });

        // Stamp right before the call: minimises elapsed time between seed and detect.
        var existing = _contextStore.TryGet(sig);
        if (existing != null)
            _contextStore.Update(sig, existing with { LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-2) });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.Signals.ContainsKey(SignalKeys.SequenceDiverged),
            "Machine-speed request must write sequence.diverged");
        var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
        Assert.True(diverged, "sequence.diverged must be true for sub-20ms machine-speed + NotFound (0.3 + 0.5 = 0.8)");
    }

    [Fact]
    public async Task MachineSpeedAloneOnExpectedState_DoesNotTrip()
    {
        // Verifies: machine-speed timing (sub-20ms) on an EXPECTED state (StaticAsset in
        // the critical phase) does NOT trip divergence. The new per-state weighting
        // intentionally requires machine-speed to combine with a meaningful unexpected
        // state, not on its own.
        const string sig = "machine-speed-expected";
        SeedDocumentContext(sig);
        var ctxBefore = _contextStore.TryGet(sig)!;
        _contextStore.Update(sig, ctxBefore with
        {
            LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-5)
        });

        var contributor = CreateContributor();
        var state = CreateState(sig, configureHttp: ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/site.css";
            ctx.Request.Headers["Sec-Fetch-Dest"] = "style";
        });

        await contributor.ContributeAsync(state, CancellationToken.None);

        var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
        var score = state.GetSignal<double>(SignalKeys.SequenceDivergenceScore);
        Assert.False(diverged, $"Machine-speed on an expected state must not trip (score={score:F2})");
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

    [Fact]
    public async Task CriticalWindow_ApiCall_WithCookie_CacheWarmFlipsImmediately()
    {
        const string sig = "returning-visitor";
        SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddSeconds(-1));

        var contributor = CreateContributor();
        // transport.protocol_class = "api" forces RequestMarkovClassifier.Classify to return ApiCall.
        // The classifier has no built-in "/api/" path heuristic, so we set the upstream signal
        // (normally written by TransportProtocolContributor) explicitly here.
        var state = CreateState(
            signature: sig,
            configureHttp: ctx =>
            {
                ctx.Request.Method = "GET";
                ctx.Request.Path = "/api/me";
                ctx.Request.Headers["Cookie"] = "session=abc";
                ctx.Request.Headers["Sec-Fetch-Dest"] = "empty";
                ctx.Request.Headers["Accept"] = "application/json";
            },
            extraSignals: new Dictionary<string, object>
            {
                [SignalKeys.TransportProtocolClass] = "api"
            });

        await contributor.ContributeAsync(state, CancellationToken.None);

        var cacheWarm = state.GetSignal<bool>(SignalKeys.SequenceCacheWarm);
        Assert.True(cacheWarm, "Returning visitor (Cookie present) should flip cache_warm in critical window");
        var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
        Assert.False(diverged, "ApiCall in critical window with Cookie + cache_warm must not diverge");
    }

    #endregion

    #region Per-State Divergence Weights

    [Fact]
    public async Task UnexpectedStaticAsset_DoesNotTripDivergence()
    {
        const string sig = "weighted-static";
        SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddSeconds(-1));

        // Push the window start into the LATE phase (>2s) so StaticAsset is unexpected
        // (late-phase expected set is [ApiCall, SignalR, WebSocket, ServerSentEvent]).
        // Under the new per-state weights, StaticAsset weight is 0.05, well below the 0.6
        // threshold, so a single static-asset miss must NOT trip divergence.
        var seeded = _contextStore.TryGet(sig)!;
        _contextStore.Update(sig, seeded with
        {
            WindowStartTime = DateTimeOffset.UtcNow.AddSeconds(-3),
            LastRequest = DateTimeOffset.UtcNow.AddSeconds(-1)
        });

        var contributor = CreateContributor();
        var state = CreateState(sig, configureHttp: ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/extra.css";
            ctx.Request.Headers["Sec-Fetch-Dest"] = "style";
        });

        await contributor.ContributeAsync(state, CancellationToken.None);

        var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
        var score = state.GetSignal<double>(SignalKeys.SequenceDivergenceScore);
        Assert.False(diverged, $"StaticAsset alone must not trip divergence (score={score:F2})");
    }

    [Fact]
    public async Task UnexpectedAuthAttempt_TripsDivergence()
    {
        const string sig = "weighted-auth";
        SeedDocumentContext(sig, lastRequest: DateTimeOffset.UtcNow.AddSeconds(-1));

        // Push LastRequest into the machine-speed band (sub-20ms) so the score lands
        // clearly above the 0.6 threshold instead of sitting exactly on the boundary:
        // AuthAttempt (0.60) + machine-speed (0.30) = 0.90, well above 0.6.
        var ctxBefore = _contextStore.TryGet(sig)!;
        _contextStore.Update(sig, ctxBefore with
        {
            LastRequest = DateTimeOffset.UtcNow.AddMilliseconds(-5)
        });

        var contributor = CreateContributor();
        // Response status 401 forces the classifier to return AuthAttempt (weight 0.60).
        // AuthAttempt is not in the critical-phase expected set, so weight applies directly.
        var state = CreateState(sig, configureHttp: ctx =>
        {
            ctx.Request.Method = "POST";
            ctx.Request.Path = "/login";
            ctx.Request.Headers["Sec-Fetch-Dest"] = "empty";
            ctx.Request.Headers["Content-Type"] = "application/x-www-form-urlencoded";
            ctx.Response.StatusCode = 401;
        });

        await contributor.ContributeAsync(state, CancellationToken.None);

        var diverged = state.GetSignal<bool>(SignalKeys.SequenceDiverged);
        Assert.True(diverged, "AuthAttempt + machine-speed should trip divergence well above the 0.6 threshold");
    }

    [Fact]
    public async Task UnexpectedStaticAsset_WithBoostedWeight_DoesTripDivergence()
    {
        // When YAML override boosts static-asset weight to 0.9, an unexpected static asset
        // SHOULD trip divergence. Proves GetWeights honours per-state YAML overrides.
        const string sig = "boosted-static";
        SeedDocumentContext(sig);
        var ctxBefore = _contextStore.TryGet(sig)!;
        _contextStore.Update(sig, ctxBefore with
        {
            WindowStartTime = DateTimeOffset.UtcNow.AddSeconds(-3),
            LastRequest = DateTimeOffset.UtcNow.AddSeconds(-1)
        });

        var contributor = CreateContributor(new Dictionary<string, object>
        {
            ["unexpected_weight_static_asset"] = 0.9
        });
        var state = CreateState(sig, configureHttp: ctx =>
        {
            ctx.Request.Method = "GET";
            ctx.Request.Path = "/extra.css";
            ctx.Request.Headers["Sec-Fetch-Dest"] = "style";
        });

        await contributor.ContributeAsync(state, CancellationToken.None);

        Assert.True(state.GetSignal<bool>(SignalKeys.SequenceDiverged),
            "Boosted static-asset weight (0.9) must trip divergence in late phase");
    }

    [Fact]
    public void YamlKeyFor_HasMappingForEveryRequestState()
    {
        // Ensures adding a RequestState enum value also requires updating YamlKeyFor.
        // If this test breaks, add the missing case to the switch AND a YAML key in
        // contentsequence.detector.yaml.
        var method = typeof(ContentSequenceContributor).GetMethod(
            "YamlKeyFor",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        foreach (var state in Enum.GetValues<RequestState>())
        {
            // Will throw ArgumentOutOfRangeException (wrapped in TargetInvocationException)
            // if a RequestState lacks a switch arm. Surface the inner cause if so.
            object? key;
            try
            {
                key = method!.Invoke(null, new object[] { state });
            }
            catch (System.Reflection.TargetInvocationException tie)
            {
                throw new Xunit.Sdk.XunitException(
                    $"YamlKeyFor has no mapping for RequestState.{state}: {tie.InnerException?.Message}");
            }
            Assert.NotNull(key);
            Assert.StartsWith("unexpected_weight_", (string)key!);
        }
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
