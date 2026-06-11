using System.Collections.Immutable;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.ContributingDetectors;

/// <summary>
///     Wave 0 (Priority 6) sequencer that tracks where each fingerprint is in its content
///     request sequence and writes <c>sequence.*</c> signals for deferred detectors.
///
///     On a document hit (Sec-Fetch-Mode: navigate OR Accept: text/html + GET):
///     - Resets the per-fingerprint <see cref="SequenceContext"/> in <see cref="SequenceContextStore"/>
///     - Loads the appropriate chain (centroid-specific Tier 2 if available, global Tier 1 fallback)
///     - Writes position = 0 signals
///
///     On continuation requests (position 1+):
///     - Classifies the request via <see cref="RequestMarkovClassifier.Classify"/>
///     - Checks for prefetch via <see cref="RequestMarkovClassifier.IsPrefetchRequest"/>
///     - Advances position and evaluates set-based divergence per phase window
///     - Writes <c>sequence.signalr_expected</c> when the next step is SignalR on a human chain
///     - Writes <c>sequence.cache_warm</c> when no StaticAsset appeared in the critical window
///     - Writes <c>sequence.prefetch_detected</c> for prefetch requests
///
///     If no signature is present, or the first request is not a document, writes NO signals —
///     deferred detectors rely on SignalNotExistsTrigger as their fallback gate.
///
///     Configuration loaded from: contentsequence.detector.yaml
///     Override via: appsettings.json → BotDetection:Detectors:ContentSequenceContributor:*
/// </summary>
public class ContentSequenceContributor : ConfiguredContributorBase, IFoundationContributor
{
    private readonly ILogger<ContentSequenceContributor> _logger;
    private readonly SequenceContextStore _contextStore;
    private readonly CentroidSequenceStore _centroidStore;
    private readonly EndpointDivergenceTracker _divergenceTracker;
    private readonly AssetHashStore? _assetHashStore;
    private readonly BotClusterService? _clusterService;

    // Phase windows (ms since window start): critical, mid, late, settled
    private static readonly double[] PhaseThresholdsMs = [500, 2000, 30_000];

    // Expected request state sets per phase
    private static readonly RequestState[][] PhaseExpectedSets =
    [
        // Critical (0-500ms): static assets + page views (preload)
        [RequestState.StaticAsset, RequestState.PageView],
        // Mid (500ms-2s): api calls also expected
        [RequestState.StaticAsset, RequestState.ApiCall, RequestState.PageView],
        // Late (2s-30s): streaming transports
        [RequestState.ApiCall, RequestState.SignalR, RequestState.WebSocket, RequestState.ServerSentEvent],
        // Settled (30s+): long-running only
        [RequestState.ApiCall, RequestState.SignalR, RequestState.ServerSentEvent]
    ];

    public ContentSequenceContributor(
        ILogger<ContentSequenceContributor> logger,
        IDetectorConfigProvider configProvider,
        SequenceContextStore contextStore,
        CentroidSequenceStore centroidStore,
        EndpointDivergenceTracker divergenceTracker,
        AssetHashStore? assetHashStore = null,
        BotClusterService? clusterService = null)
        : base(configProvider)
    {
        _logger = logger;
        _contextStore = contextStore;
        _centroidStore = centroidStore;
        _divergenceTracker = divergenceTracker;
        _assetHashStore = assetHashStore;
        _clusterService = clusterService;
    }

    public override string Name => "ContentSequence";
    // Wave 6 -- AFTER TransportProtocolContributor (wave 5) so RequestMarkovClassifier
    // can read TransportIsSignalR / IsUpgrade / ProtocolClass. Previously wave 4, which
    // ran before transport classification and misclassified SignalR negotiates as
    // PageView. The sequence-tracking logic itself doesn't depend on wave 4 vs 6.
    public override int Priority => Manifest?.Priority ?? 6;

    // No triggers — runs immediately in Wave 0
    public override IReadOnlyList<TriggerCondition> TriggerConditions => Array.Empty<TriggerCondition>();

    // Config-driven parameters
    private double DivergenceThreshold => GetParam("divergence_threshold", 0.6);
    private double TimingToleranceMultiplier => GetParam("timing_tolerance_multiplier", 3.0);
    private int MinCentroidSampleSize => GetParam("min_centroid_sample_size", 20);
    private int SessionGapMinutes => GetParam("session_gap_minutes", 30);
    private int MaxTrackedPositions => GetParam("max_tracked_positions", 20);
    private double MachineSpeedThresholdMs => GetParam("machine_speed_threshold_ms", 20.0);
    private double MachineSpeedScore => GetParam("machine_speed_score", 0.3);
    private double HighRequestCountScore => GetParam("high_request_count_score", 0.2);
    private int HighRequestCountThreshold => GetParam("high_request_count_threshold", 200);
    private int RequestCountIdleResetSeconds => GetParam("request_count_idle_reset_seconds", 60);

    public override Task<IReadOnlyList<DetectionContribution>> ContributeAsync(
        BlackboardState state,
        CancellationToken cancellationToken = default)
    {
        // Publish the Markov classification for the current request BEFORE any of
        // the sequence-context logic short-circuits. The orchestrator's per-request
        // persistence path (BlackboardOrchestrator.TryPersistRequest) reads this
        // signal to write the markov_state column, and downstream consumers expect
        // it on every request -- not just continuation requests with an active
        // sequence. Idempotent: SessionVectorContributor at wave 30 may overwrite
        // with the same value, which is fine.
        try
        {
            var requestState = RequestMarkovClassifier.Classify(state);
            state.WriteSignal(SignalKeys.SessionCurrentState, requestState.ToString());
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ContentSequence: failed to classify+publish markov state");
        }

        // Require a primary signature — without it, no session context is possible
        var signature = state.GetSignal<string>(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
        {
            _logger.LogDebug("ContentSequence: no primary signature, skipping");
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
        }

        var request = state.HttpContext.Request;
        var isDocumentRequest = IsDocumentRequest(request, state);

        // Get or create sequence context for this fingerprint
        var ctx = _contextStore.GetOrCreate(signature, SessionGapMinutes);

        if (isDocumentRequest)
            return Task.FromResult(HandleDocumentRequest(state, signature, request, ctx));

        // Not a document request — only continue if we have an active sequence context
        // No active sequence: fresh context with empty chain + not a document → write nothing
        // Deferred detectors will run via SignalNotExistsTrigger fallback
        if (!isDocumentRequest && ctx.ExpectedChain.Length == 0)
        {
            _logger.LogDebug("ContentSequence: no active sequence for {Signature}, non-document first request", signature);
            return Task.FromResult<IReadOnlyList<DetectionContribution>>(Array.Empty<DetectionContribution>());
        }

        return Task.FromResult(HandleContinuationRequest(state, signature, request, ctx));
    }

    /// <summary>
    ///     Determines whether the incoming request is a top-level document navigation.
    ///     Priority order:
    ///     1. Sec-Fetch-Mode: navigate (primary — Fetch Metadata, W3C spec)
    ///     2. Accept: text/html + GET method (fallback for older browsers)
    ///     3. transport.protocol_class == "document" (opportunistic from TransportProtocolContributor)
    /// </summary>
    private static bool IsDocumentRequest(HttpRequest request, BlackboardState state)
    {
        // 1. Fetch Metadata: navigate mode is definitive
        var secFetchMode = request.Headers["Sec-Fetch-Mode"].FirstOrDefault();
        if (string.Equals(secFetchMode, "navigate", StringComparison.OrdinalIgnoreCase))
            return true;

        // 2. Accept header + GET method
        if (HttpMethods.IsGet(request.Method))
        {
            var accept = request.Headers.Accept.ToString();
            if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 3. Transport protocol class signal (opportunistic)
        var protocolClass = state.GetSignal<string>(SignalKeys.TransportProtocolClass);
        if (string.Equals(protocolClass, "document", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    ///     Handles a document (position 0) hit: resets sequence context, loads chain, writes signals.
    /// </summary>
    private IReadOnlyList<DetectionContribution> HandleDocumentRequest(
        BlackboardState state,
        string signature,
        HttpRequest request,
        SequenceContext ctx)
    {
        // Resolve the best available chain for this fingerprint
        var (chain, centroidId, isReady) = ResolveChain(signature);

        var contentPath = request.Path.Value ?? "/";

        // Build fresh context at position 0
        var newCtx = ctx with
        {
            Position = 0,
            ExpectedChain = chain.ExpectedStates,
            TypicalGapsMs = chain.TypicalGapsMs,
            GapToleranceMs = chain.GapToleranceMs,
            CentroidId = centroidId,
            CentroidType = chain.Type,
            WindowStartTime = DateTimeOffset.UtcNow,
            RequestCountInWindow = 1,
            LastRequest = DateTimeOffset.UtcNow,
            ObservedStateSet = ImmutableHashSet<RequestState>.Empty,
            HasDiverged = false,
            DivergenceCount = 0,
            CacheWarm = false,
            ContentPath = contentPath
        };
        _contextStore.Update(signature, newCtx);

        // Track session for divergence rate analysis
        _divergenceTracker.RecordSession(contentPath);

        // Check if this path's asset hash changed recently (deploy happened)
        var assetChanged = _assetHashStore?.IsRecentlyChanged(contentPath) ?? false;
        // Treat a not-yet-learned global as stale: suppresses divergence scoring downstream.
        var centroidStale = _centroidStore.IsEndpointStale(contentPath) || !isReady;

        _logger.LogDebug(
            "ContentSequence: document hit for {Signature}, chain={ChainId}, centroid={CentroidId}",
            signature, newCtx.ChainId, centroidId);

        state.WriteSignals([
            new(SignalKeys.SequencePosition, 0),
            new(SignalKeys.SequenceOnTrack, true),
            new(SignalKeys.SequenceDiverged, false),
            new(SignalKeys.SequenceDivergenceScore, 0.0),
            new(SignalKeys.SequenceChainId, newCtx.ChainId),
            new(SignalKeys.SequenceCentroidType, chain.Type.ToString()),
            new(SignalKeys.SequenceContentPath, contentPath),
            new(SignalKeys.SequenceCentroidStale, centroidStale),
            new(SignalKeys.AssetContentChanged, assetChanged)
        ]);

        return new[] { NeutralContribution("Sequence", $"Document hit; sequence reset at {contentPath}") };
    }

    /// <summary>
    ///     Handles a continuation request (position 1+): classifies, checks prefetch, advances position,
    ///     evaluates phase-window divergence, and writes sequence signals.
    /// </summary>
    private IReadOnlyList<DetectionContribution> HandleContinuationRequest(
        BlackboardState state,
        string signature,
        HttpRequest request,
        SequenceContext ctx)
    {
        var isPrefetch = RequestMarkovClassifier.IsPrefetchRequest(request);
        var requestState = RequestMarkovClassifier.Classify(state);
        var now = DateTimeOffset.UtcNow;

        // Idle-reset: if the inter-request gap exceeded the configured threshold, treat this
        // as a fresh window before scoring. The new window starts at `now`, the observed-state
        // set is reseeded with the current request's state, and the request count and
        // cache-warm flag are reset. Position, ChainId, and divergence counters are session-
        // scoped and intentionally NOT reset. Computed BEFORE divergence scoring so the
        // first request after idle does not see the stale high request count or stale
        // WindowStartTime (which would mis-categorise its phase).
        var idleSeconds = (now - ctx.LastRequest).TotalSeconds;
        var resetWindow = idleSeconds >= RequestCountIdleResetSeconds;

        var effectiveWindowStart = resetWindow ? now : ctx.WindowStartTime;
        var effectiveRequestCount = resetWindow ? 1 : ctx.RequestCountInWindow + 1;
        var effectiveObservedSetIn = resetWindow ? ImmutableHashSet<RequestState>.Empty : ctx.ObservedStateSet;
        var initialCacheWarm = !resetWindow && ctx.CacheWarm;

        var elapsedMs = (now - effectiveWindowStart).TotalMilliseconds;
        var position = Math.Min(ctx.Position + 1, MaxTrackedPositions);

        // Track observed states (prefetch requests are recorded but not used for divergence scoring)
        var observedSet = effectiveObservedSetIn.Add(requestState);

        // Phase window detection (uses the fresh window start when reset)
        var phaseIndex = GetPhaseIndex(elapsedMs);
        var expectedSet = PhaseExpectedSets[phaseIndex];

        // Cache warm detection:
        //  1. critical window closed with no StaticAsset observed (returning visitor whose
        //     browser skipped statics), OR
        //  2. returning visitor signalled by a Cookie header and the first continuation is
        //     not a StaticAsset (warm cache means the XHR fires before any static reload).
        var cacheWarm = initialCacheWarm;
        var hasCookie = request.Headers.ContainsKey("Cookie");
        if (!cacheWarm)
        {
            if (phaseIndex > 0 && !observedSet.Contains(RequestState.StaticAsset))
                cacheWarm = true;
            else if (hasCookie && requestState != RequestState.StaticAsset)
                cacheWarm = true;
        }

        // Divergence scoring (skip for prefetch requests). Uses a synthetic ctx view where
        // RequestCountInWindow reflects the post-reset count so the high-count penalty does
        // not fire on the first request back from an idle gap.
        // Suppress scoring entirely when the global baseline is still warming up.
        // ctx.CentroidType is Unknown for global-chain users; the IsGlobalReady check
        // distinguishes a warming-up baseline from a cluster-specific chain.
        var scoringAllowed = ctx.CentroidType != CentroidType.Unknown || _centroidStore.IsGlobalReady;
        double divergenceScore = 0.0;
        if (!isPrefetch && scoringAllowed)
            divergenceScore = ComputeDivergenceScore(
                requestState, elapsedMs, expectedSet,
                ctx with { RequestCountInWindow = effectiveRequestCount },
                cacheWarm);

        var hasDiverged = divergenceScore >= DivergenceThreshold;
        var divergenceCount = ctx.DivergenceCount + (hasDiverged && !ctx.HasDiverged ? 1 : 0);

        // On divergence: record for staleness tracking; if rate exceeds threshold, mark centroid stale
        if (hasDiverged)
        {
            var contentPath = ctx.ContentPath;
            if (!string.IsNullOrEmpty(contentPath))
            {
                _divergenceTracker.RecordDivergence(contentPath);
                if (_divergenceTracker.IsStale(contentPath))
                {
                    _centroidStore.MarkEndpointStale(contentPath);
                    _divergenceTracker.Reset(contentPath);
                    _logger.LogInformation(
                        "ContentSequence: divergence rate threshold exceeded for {Path} — marking centroid stale",
                        contentPath);
                }
            }
        }

        // SignalR expected: next step in chain is SignalR AND centroid is not Bot
        var signalRExpected = IsSignalRExpected(ctx, position);

        var updatedCtx = ctx with
        {
            Position = position,
            ObservedStateSet = observedSet,
            WindowStartTime = effectiveWindowStart,
            RequestCountInWindow = effectiveRequestCount,
            LastRequest = now,
            HasDiverged = hasDiverged,
            DivergenceCount = divergenceCount,
            CacheWarm = cacheWarm
        };
        _contextStore.Update(signature, updatedCtx);

        _logger.LogDebug(
            "ContentSequence: position={Position}, state={State}, phase={Phase}, divergence={Score:F2}, prefetch={IsPrefetch}",
            position, requestState, phaseIndex, divergenceScore, isPrefetch);

        // Write signals
        state.WriteSignals([
            new(SignalKeys.SequencePosition, position),
            new(SignalKeys.SequenceOnTrack, !hasDiverged),
            new(SignalKeys.SequenceDiverged, hasDiverged),
            new(SignalKeys.SequenceDivergenceScore, divergenceScore),
            new(SignalKeys.SequenceChainId, ctx.ChainId),
            new(SignalKeys.SequenceCentroidType, ctx.CentroidType.ToString()),
            new(SignalKeys.SequenceCacheWarm, cacheWarm),
            new(SignalKeys.SequenceCentroidStale, _centroidStore.IsEndpointStale(ctx.ContentPath))
        ]);

        if (isPrefetch)
            state.WriteSignal(SignalKeys.SequencePrefetchDetected, true);

        if (signalRExpected)
            state.WriteSignal(SignalKeys.SequenceSignalRExpected, true);

        if (hasDiverged)
        {
            state.WriteSignal(SignalKeys.SequenceDivergenceAtPosition, position);
            return new[] { NeutralContribution("Sequence", $"Sequence diverged at position {position} (score={divergenceScore:F2})") };
        }

        return new[] { NeutralContribution("Sequence", $"Sequence on track at position {position}") };
    }

    /// <summary>
    ///     Returns the phase index (0=critical, 1=mid, 2=late, 3=settled) based on elapsed ms.
    /// </summary>
    private static int GetPhaseIndex(double elapsedMs)
    {
        for (var i = 0; i < PhaseThresholdsMs.Length; i++)
        {
            if (elapsedMs < PhaseThresholdsMs[i])
                return i;
        }
        return PhaseThresholdsMs.Length; // settled phase (index 3)
    }

    /// <summary>
    ///     Computes a divergence score for the current request based on:
    ///     - Machine-speed timing (&lt; MachineSpeedThresholdMs inter-request)
    ///     - State not in expected set for the current phase, weighted by RequestState
    ///     - High request volume in window (&gt; HighRequestCountThreshold)
    ///     Score is capped at 1.0.
    /// </summary>
    private double ComputeDivergenceScore(
        RequestState requestState,
        double elapsedMs,
        RequestState[] expectedSet,
        SequenceContext ctx,
        bool cacheWarm)
    {
        double score = 0.0;
        var weights = GetWeights();

        // Machine-speed timing: sub-threshold ms between requests is bot-like
        var msSinceLastRequest = (DateTimeOffset.UtcNow - ctx.LastRequest).TotalMilliseconds;
        if (msSinceLastRequest < MachineSpeedThresholdMs)
            score += MachineSpeedScore;

        // State not in expected set for this phase.
        // Score is now per-state (was a flat 0.5, a major false-positive source).
        // Exception: if cache-warm and ApiCall in critical window, don't penalise.
        var isExpected = expectedSet.Contains(requestState);
        if (!isExpected)
        {
            var isCacheWarmException = cacheWarm && requestState == RequestState.ApiCall;
            if (!isCacheWarmException)
                score += weights.For(requestState);
        }

        // High request volume in window
        if (ctx.RequestCountInWindow > HighRequestCountThreshold)
            score += HighRequestCountScore;

        return Math.Min(score, 1.0);
    }

    // Cached at contributor scope. Wave 0 runs on every request and the sidecar
    // p99 detection budget is 10ms; recomputing 10 GetParam calls per request
    // burns budget for no behavioural benefit, since ConfiguredContributorBase.Config
    // (Weights, Confidence, etc.) is itself pinned with the same ??= pattern and
    // already bounded by the same hot-reload semantics. A future InvalidateCache
    // extension to push contributor-scope refresh should reset both this field
    // and the base class's _cachedConfig together.
    private StateDivergenceWeights? _weights;

    private StateDivergenceWeights GetWeights() =>
        _weights ??= StateDivergenceWeights.FromParameters((state, fallback) =>
            GetParam(YamlKeyFor(state), fallback));

    private static string YamlKeyFor(RequestState state) => state switch
    {
        RequestState.StaticAsset => "unexpected_weight_static_asset",
        RequestState.PageView => "unexpected_weight_page_view",
        RequestState.ApiCall => "unexpected_weight_api_call",
        RequestState.SignalR => "unexpected_weight_signalr",
        RequestState.WebSocket => "unexpected_weight_websocket",
        RequestState.ServerSentEvent => "unexpected_weight_server_sent_event",
        RequestState.FormSubmit => "unexpected_weight_form_submit",
        RequestState.AuthAttempt => "unexpected_weight_auth_attempt",
        RequestState.NotFound => "unexpected_weight_not_found",
        RequestState.Search => "unexpected_weight_search",
        _ => throw new ArgumentOutOfRangeException(
            nameof(state), state,
            "RequestState has no YAML weight key mapping. Add a YAML key in contentsequence.detector.yaml and a switch arm in YamlKeyFor.")
    };

    /// <summary>
    ///     Returns true when the next expected chain state is SignalR AND the centroid is not Bot.
    ///     Used by downstream detectors to avoid false-positive flagging of expected SignalR upgrades.
    /// </summary>
    private static bool IsSignalRExpected(SequenceContext ctx, int nextPosition)
    {
        if (ctx.CentroidType == CentroidType.Bot)
            return false;

        var chain = ctx.ExpectedChain;
        if (chain.Length == 0)
            return false;

        // Look at the state expected at the next position (bounded)
        var lookAheadIndex = Math.Min(nextPosition, chain.Length - 1);
        return chain[lookAheadIndex] == RequestState.SignalR;
    }

    /// <summary>
    ///     Resolves the best available chain for a fingerprint.
    ///     Priority: centroid-specific chain (Tier 2) then global chain (Tier 1).
    ///     The centroid is discovered via <see cref="BotClusterService.FindCluster"/> (optional service).
    ///     The <c>isReady</c> flag is true for any cluster-specific chain; it reflects
    ///     <see cref="CentroidSequenceStore.IsGlobalReady"/> for the global-chain fallback so callers
    ///     can suppress divergence scoring while the site-learned baseline warms up.
    /// </summary>
    private (CentroidSequence chain, string centroidId, bool isReady) ResolveChain(string signature)
    {
        if (_clusterService != null)
        {
            var cluster = _clusterService.FindCluster(signature);
            if (cluster != null)
            {
                var centroidChain = _centroidStore.TryGetCentroidChain(
                    cluster.ClusterId, MinCentroidSampleSize);
                if (centroidChain != null)
                {
                    _logger.LogDebug(
                        "ContentSequence: using centroid chain {CentroidId} (type={Type}, samples={Samples})",
                        centroidChain.CentroidId, centroidChain.Type, centroidChain.SampleSize);
                    return (centroidChain, centroidChain.CentroidId, true);
                }
            }
        }

        return (_centroidStore.GlobalChain, "global", _centroidStore.IsGlobalReady);
    }
}
