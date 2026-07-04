using System.Collections.Immutable;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     SensorAtom + ConstrainerAtom (per Taxonomy.md) that tracks each
///     fingerprint's position in its content request sequence, publishes the
///     canonical <c>sequence.*</c> signals, and lets deferred detectors
///     honour on-track / diverged / cache-warm carve-outs.
/// </summary>
/// <remarks>
///     <para>
///         Native <see cref="IDetectorAtom"/> replacement for
///         <c>ContentSequenceContributor</c>. Priority 6 -- Wave 0, must run
///         after the transport-protocol atom so RequestMarkovClassifier can
///         read TransportIsSignalR / IsUpgrade / ProtocolClass.
///     </para>
///     <para>
///         RequestMarkovClassifier exposes a sink-native
///         <c>Classify(HttpContext, SignalSink)</c> overload that reads the
///         transport / response hints off the sink directly, so no
///         BlackboardState shim is needed.
///     </para>
/// </remarks>
public sealed class ContentSequenceAtom : DetectorAtomBase
{
    private static readonly double[] PhaseThresholdsMs = [500, 2000, 30_000];

    private static readonly RequestState[][] PhaseExpectedSets =
    [
        [RequestState.StaticAsset, RequestState.PageView],
        [RequestState.StaticAsset, RequestState.ApiCall, RequestState.PageView],
        [RequestState.ApiCall, RequestState.SignalR, RequestState.WebSocket, RequestState.ServerSentEvent],
        [RequestState.ApiCall, RequestState.SignalR, RequestState.ServerSentEvent]
    ];

    private readonly ILogger<ContentSequenceAtom> _logger;
    private readonly SequenceContextStore _contextStore;
    private readonly CentroidSequenceStore _centroidStore;
    private readonly EndpointDivergenceTracker _divergenceTracker;
    private readonly AssetHashStore? _assetHashStore;
    private readonly BotClusterService? _clusterService;
    private readonly IDetectorConfigProvider _configProvider;
    private readonly IHttpContextAccessor _httpContextAccessor;

    private StateDivergenceWeights? _weights;

    public ContentSequenceAtom(
        ILogger<ContentSequenceAtom> logger,
        IDetectorConfigProvider configProvider,
        IHttpContextAccessor httpContextAccessor,
        SequenceContextStore contextStore,
        CentroidSequenceStore centroidStore,
        EndpointDivergenceTracker divergenceTracker,
        AssetHashStore? assetHashStore = null,
        BotClusterService? clusterService = null)
        : base(name: "ContentSequence", category: "Sequence")
    {
        _logger = logger;
        _configProvider = configProvider;
        _httpContextAccessor = httpContextAccessor;
        _contextStore = contextStore;
        _centroidStore = centroidStore;
        _divergenceTracker = divergenceTracker;
        _assetHashStore = assetHashStore;
        _clusterService = clusterService;
    }

    public override int Priority => 6;
    public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

    private double DivergenceThreshold => _configProvider.GetParameter(Name, "divergence_threshold", 0.6);
    private double TimingToleranceMultiplier => _configProvider.GetParameter(Name, "timing_tolerance_multiplier", 3.0);
    private int MinCentroidSampleSize => _configProvider.GetParameter(Name, "min_centroid_sample_size", 20);
    private int SessionGapMinutes => _configProvider.GetParameter(Name, "session_gap_minutes", 30);
    private int MaxTrackedPositions => _configProvider.GetParameter(Name, "max_tracked_positions", 20);
    private double MachineSpeedThresholdMs => _configProvider.GetParameter(Name, "machine_speed_threshold_ms", 20.0);
    private double MachineSpeedScore => _configProvider.GetParameter(Name, "machine_speed_score", 0.3);
    private double HighRequestCountScore => _configProvider.GetParameter(Name, "high_request_count_score", 0.2);
    private int HighRequestCountThreshold => _configProvider.GetParameter(Name, "high_request_count_threshold", 200);
    private int RequestCountIdleResetSeconds => _configProvider.GetParameter(Name, "request_count_idle_reset_seconds", 60);

    public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
        SignalSink sink,
        string sessionId,
        CancellationToken ct = default)
    {
        var context = _httpContextAccessor.HttpContext;
        if (context is null) return Task.FromResult(None());

        // Publish current-request markov classification BEFORE any sequence-context
        // short-circuit. Downstream persistence + SessionVector rely on this hint
        // being present on every request.
        try
        {
            var currentState = RequestMarkovClassifier.Classify(context, sink);
            sink.Raise($"{SignalKeys.SessionCurrentState}:{currentState}", sessionId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "ContentSequence: failed to classify+publish markov state");
        }

        var signature = sink.ReadHint(SignalKeys.PrimarySignature);
        if (string.IsNullOrEmpty(signature))
        {
            _logger.LogDebug("ContentSequence: no primary signature, skipping");
            return Task.FromResult(None());
        }

        var request = context.Request;
        var isDocumentRequest = IsDocumentRequest(request, sink);
        var ctx = _contextStore.GetOrCreate(signature, SessionGapMinutes);

        if (isDocumentRequest)
            return Task.FromResult(HandleDocumentRequest(sink, sessionId, signature, request, ctx));

        if (ctx.ExpectedChain.Length == 0)
        {
            _logger.LogDebug("ContentSequence: no active sequence for {Signature}, non-document first request", signature);
            return Task.FromResult(None());
        }

        return Task.FromResult(HandleContinuationRequest(sink, sessionId, signature, request, ctx, context));
    }

    private static bool IsDocumentRequest(HttpRequest request, SignalSink sink)
    {
        var secFetchMode = request.Headers["Sec-Fetch-Mode"].FirstOrDefault();
        if (string.Equals(secFetchMode, "navigate", StringComparison.OrdinalIgnoreCase))
            return true;

        if (HttpMethods.IsGet(request.Method))
        {
            var accept = request.Headers.Accept.ToString();
            if (accept.Contains("text/html", StringComparison.OrdinalIgnoreCase)) return true;
        }

        var protocolClass = sink.ReadHint(SignalKeys.TransportProtocolClass);
        return string.Equals(protocolClass, "document", StringComparison.OrdinalIgnoreCase);
    }

    private IReadOnlyList<DetectionContribution> HandleDocumentRequest(
        SignalSink sink, string sessionId, string signature, HttpRequest request, SequenceContext ctx)
    {
        var (chain, centroidId, isReady) = ResolveChain(signature);
        var contentPath = request.Path.Value ?? "/";

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
        _divergenceTracker.RecordSession(contentPath);

        var assetChanged = _assetHashStore?.IsRecentlyChanged(contentPath) ?? false;
        var centroidStale = _centroidStore.IsEndpointStale(contentPath) || !isReady;

        _logger.LogDebug(
            "ContentSequence: document hit for {Signature}, chain={ChainId}, centroid={CentroidId}",
            signature, newCtx.ChainId, centroidId);

        sink.Raise($"{SignalKeys.SequencePosition}:0", sessionId);
        sink.Raise($"{SignalKeys.SequenceOnTrack}:true", sessionId);
        sink.Raise($"{SignalKeys.SequenceDiverged}:false", sessionId);
        sink.Raise($"{SignalKeys.SequenceDivergenceScore}:0", sessionId);
        sink.Raise($"{SignalKeys.SequenceChainId}:{newCtx.ChainId}", sessionId);
        sink.Raise($"{SignalKeys.SequenceCentroidType}:{chain.Type}", sessionId);
        sink.Raise($"{SignalKeys.SequenceContentPath}:{contentPath}", sessionId);
        sink.Raise($"{SignalKeys.SequenceCentroidStale}:{(centroidStale ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.AssetContentChanged}:{(assetChanged ? "true" : "false")}", sessionId);

        return Single(DetectionContribution.Info(Name, Category, $"Document hit; sequence reset at {contentPath}"));
    }

    private IReadOnlyList<DetectionContribution> HandleContinuationRequest(
        SignalSink sink, string sessionId, string signature, HttpRequest request, SequenceContext ctx, HttpContext context)
    {
        var isPrefetch = RequestMarkovClassifier.IsPrefetchRequest(request);
        var requestState = RequestMarkovClassifier.Classify(context, sink);
        var now = DateTimeOffset.UtcNow;

        var idleSeconds = (now - ctx.LastRequest).TotalSeconds;
        var resetWindow = idleSeconds >= RequestCountIdleResetSeconds;

        var effectiveWindowStart = resetWindow ? now : ctx.WindowStartTime;
        var effectiveRequestCount = resetWindow ? 1 : ctx.RequestCountInWindow + 1;
        var effectiveObservedSetIn = resetWindow ? ImmutableHashSet<RequestState>.Empty : ctx.ObservedStateSet;
        var initialCacheWarm = !resetWindow && ctx.CacheWarm;

        var elapsedMs = (now - effectiveWindowStart).TotalMilliseconds;
        var position = Math.Min(ctx.Position + 1, MaxTrackedPositions);

        var observedSet = effectiveObservedSetIn.Add(requestState);
        var phaseIndex = GetPhaseIndex(elapsedMs);
        var expectedSet = PhaseExpectedSets[phaseIndex];

        var cacheWarm = initialCacheWarm;
        var hasCookie = request.Headers.ContainsKey("Cookie");
        if (!cacheWarm)
        {
            if (phaseIndex > 0 && !observedSet.Contains(RequestState.StaticAsset))
                cacheWarm = true;
            else if (hasCookie && requestState != RequestState.StaticAsset)
                cacheWarm = true;
        }

        var scoringAllowed = ctx.CentroidType != CentroidType.Unknown || _centroidStore.IsGlobalReady;
        double divergenceScore = 0.0;
        if (!isPrefetch && scoringAllowed)
        {
            divergenceScore = ComputeDivergenceScore(
                requestState, elapsedMs, expectedSet,
                ctx with { RequestCountInWindow = effectiveRequestCount },
                cacheWarm);
        }

        var hasDiverged = divergenceScore >= DivergenceThreshold;
        var divergenceCount = ctx.DivergenceCount + (hasDiverged && !ctx.HasDiverged ? 1 : 0);

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

        sink.Raise($"{SignalKeys.SequencePosition}:{position}", sessionId);
        sink.Raise($"{SignalKeys.SequenceOnTrack}:{(!hasDiverged ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.SequenceDiverged}:{(hasDiverged ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.SequenceDivergenceScore}:{divergenceScore.ToString("F4", CultureInfo.InvariantCulture)}", sessionId);
        sink.Raise($"{SignalKeys.SequenceChainId}:{ctx.ChainId}", sessionId);
        sink.Raise($"{SignalKeys.SequenceCentroidType}:{ctx.CentroidType}", sessionId);
        sink.Raise($"{SignalKeys.SequenceCacheWarm}:{(cacheWarm ? "true" : "false")}", sessionId);
        sink.Raise($"{SignalKeys.SequenceCentroidStale}:{(_centroidStore.IsEndpointStale(ctx.ContentPath) ? "true" : "false")}", sessionId);

        if (isPrefetch) sink.Raise($"{SignalKeys.SequencePrefetchDetected}:true", sessionId);
        if (signalRExpected) sink.Raise($"{SignalKeys.SequenceSignalRExpected}:true", sessionId);

        if (hasDiverged)
        {
            sink.Raise($"{SignalKeys.SequenceDivergenceAtPosition}:{position}", sessionId);
            return Single(DetectionContribution.Info(Name, Category, $"Sequence diverged at position {position} (score={divergenceScore:F2})"));
        }

        return Single(DetectionContribution.Info(Name, Category, $"Sequence on track at position {position}"));
    }

    private static int GetPhaseIndex(double elapsedMs)
    {
        for (var i = 0; i < PhaseThresholdsMs.Length; i++)
            if (elapsedMs < PhaseThresholdsMs[i]) return i;
        return PhaseThresholdsMs.Length;
    }

    private double ComputeDivergenceScore(
        RequestState requestState, double elapsedMs, RequestState[] expectedSet,
        SequenceContext ctx, bool cacheWarm)
    {
        double score = 0.0;
        var weights = GetWeights();

        var msSinceLastRequest = (DateTimeOffset.UtcNow - ctx.LastRequest).TotalMilliseconds;
        if (msSinceLastRequest < MachineSpeedThresholdMs) score += MachineSpeedScore;

        var isExpected = expectedSet.Contains(requestState);
        if (!isExpected)
        {
            var isCacheWarmException = cacheWarm && requestState == RequestState.ApiCall;
            if (!isCacheWarmException) score += weights.For(requestState);
        }

        if (ctx.RequestCountInWindow > HighRequestCountThreshold) score += HighRequestCountScore;

        return Math.Min(score, 1.0);
    }

    private StateDivergenceWeights GetWeights() =>
        _weights ??= StateDivergenceWeights.FromParameters((state, fallback) =>
            _configProvider.GetParameter(Name, YamlKeyFor(state), fallback));

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

    private static bool IsSignalRExpected(SequenceContext ctx, int nextPosition)
    {
        if (ctx.CentroidType == CentroidType.Bot) return false;
        var chain = ctx.ExpectedChain;
        if (chain.Length == 0) return false;
        var lookAheadIndex = Math.Min(nextPosition, chain.Length - 1);
        return chain[lookAheadIndex] == RequestState.SignalR;
    }

    private (CentroidSequence chain, string centroidId, bool isReady) ResolveChain(string signature)
    {
        if (_clusterService is not null)
        {
            var cluster = _clusterService.FindCluster(signature);
            if (cluster is not null)
            {
                var centroidChain = _centroidStore.TryGetCentroidChain(cluster.ClusterId, MinCentroidSampleSize);
                if (centroidChain is not null)
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
