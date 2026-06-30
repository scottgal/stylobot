using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration.Lanes;
using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Signature-level response coordinator for a single signature.
///     Receives a shared SignalSink from SignatureResponseCoordinatorCache - does NOT own it.
///     Manages cross-request response state and lanes for that signature.
///     Uses WaveAtom for parallel lane execution.
/// </summary>
public sealed class SignatureResponseCoordinator : IAsyncDisposable
{
    private readonly BehavioralLane _behavioralLane;
    private readonly SpectralLane _spectralLane;
    private readonly ReputationLane _reputationLane;
    private readonly IReadOnlyList<IAnalysisLane> _lanes;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger;
    private readonly string _signature;
    private readonly SignalSink _sink;
    private readonly LinkedList<OperationCompleteSignal> _window;
    private double _maxRiskSeen;

    /// <summary>
    ///     Sink is provided by the cache (shared across all coordinators).
    /// </summary>
    internal SignalSink Sink => _sink;

    /// <summary>
    ///     Returns the highest aggregated risk score seen by this coordinator.
    ///     Used by the SlidingCacheAtom retention scorer during eviction; a slightly stale value is acceptable.
    /// </summary>
    internal double GetRiskScore() => Volatile.Read(ref _maxRiskSeen);

    public SignatureResponseCoordinator(
        string signature,
        ILogger logger,
        SignalSink sharedSink,
        UpstreamHealthGate? upstreamHealth = null)
    {
        _signature = signature;
        _logger = logger;

        // Sink is shared - NOT owned by this coordinator
        _sink = sharedSink;

        _window = new LinkedList<OperationCompleteSignal>();

        // Each lane is given a coordinator-scoped key so their signals in the shared sink
        // cannot be confused with signals from a different coordinator's lanes.
        // The key is "c.{signature}" - short enough to avoid key bloat but unique per coordinator.
        var coordinatorKey = $"c.{_signature}";

        _behavioralLane = new BehavioralLane(_sink, coordinatorKey);
        _spectralLane = new SpectralLane(_sink, coordinatorKey);
        // ReputationLane stands down its 404/403 indicators when the
        // upstream-health gate reports outage; honeypot + 429 remain.
        _reputationLane = new ReputationLane(_sink, coordinatorKey, upstreamHealth);
        _lanes = [_behavioralLane, _spectralLane, _reputationLane];

        _logger.LogDebug("SignatureResponseCoordinator created for {Signature} with {LaneCount} lanes",
            signature, _lanes.Count);
    }

    public async ValueTask DisposeAsync()
    {
        // _sink is NOT owned by this coordinator - do not dispose it here.
        // The cache (SignatureResponseCoordinatorCache) owns and disposes the shared sink.
        _lock.Dispose();

        _logger.LogDebug("SignatureResponseCoordinator disposed for {Signature}", _signature);
        await ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Receive early request escalation.
    /// </summary>
    public async Task ReceiveRequestAsync(
        RequestCompleteSignal signal,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Emit notification signal (lanes can query state when they see this)
            _sink.Raise("request.early.arrived", signal.RequestId);

            // Track highest risk seen (also covers early request signals before lane results)
            if (signal.Risk > _maxRiskSeen)
                Volatile.Write(ref _maxRiskSeen, signal.Risk);

            // If honeypot or very high risk, emit heuristic feedback signals
            if (signal.Honeypot || signal.Risk > 0.8)
            {
                _logger.LogWarning(
                    "Early high-risk signal for {Signature}: risk={Risk:F2}, honeypot={Honeypot}",
                    _signature, signal.Risk, signal.Honeypot);

                // Emit heuristic feedback signals for learning handlers
                _sink.Raise("heuristic.feedback.signature", _signature);
                _sink.Raise("heuristic.feedback.risk", signal.Risk.ToString("F4"));
                _sink.Raise("heuristic.feedback.honeypot", signal.Honeypot.ToString());
                _sink.Raise("heuristic.feedback.path", signal.Path ?? "/");
                _sink.Raise("heuristic.feedback.datacenter", signal.Datacenter ?? "unknown");

                // Signal high-confidence bot detection for immediate learning
                if (signal.Honeypot)
                {
                    _sink.Raise("learning.high_confidence_bot", _signature);
                    _sink.Raise("learning.feedback.type", "honeypot_hit");
                }
                else if (signal.Risk > 0.9)
                {
                    _sink.Raise("learning.high_risk_signature", _signature);
                    _sink.Raise("learning.feedback.type", "high_risk");
                }
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>
    ///     Receive operation complete escalation and run lanes in parallel using WaveAtom.
    /// </summary>
    public async Task ReceiveOperationAsync(
        OperationCompleteSignal signal,
        CancellationToken cancellationToken)
    {
        await _lock.WaitAsync(cancellationToken);
        try
        {
            // Add to window
            _window.AddLast(signal);
            while (_window.Count > 100)
                _window.RemoveFirst();

            // Emit notification signal (lanes react to this)
            _sink.Raise("operation.added", signal.RequestId);

            // Create snapshot for lane processing
            var windowSnapshot = _window.ToList();

            // Run lanes in parallel
            var laneTasks = _lanes.Select(lane => lane.AnalyzeAsync(windowSnapshot, cancellationToken));
            await Task.WhenAll(laneTasks);

            // Aggregate lane signals from own sink
            var behavior = AggregateLaneSignals();

            // Track highest risk score seen for retention scoring during cache eviction
            if (behavior.Score > _maxRiskSeen)
                Volatile.Write(ref _maxRiskSeen, behavior.Score);

            // Emit aggregated signature behavior as granular signals
            _sink.Raise("signature.behavior.score", behavior.Score.ToString("F4"));
            _sink.Raise("signature.behavior.behavioral", behavior.BehavioralScore.ToString("F4"));
            _sink.Raise("signature.behavior.spectral", behavior.SpectralScore.ToString("F4"));
            _sink.Raise("signature.behavior.reputation", behavior.ReputationScore.ToString("F4"));
            _sink.Raise("signature.behavior.windowsize", behavior.WindowSize.ToString());

            _logger.LogDebug(
                "Signature behavior for {Signature}: score={Score:F2}, window={Count}",
                _signature, behavior.Score, _window.Count);
        }
        finally
        {
            _lock.Release();
        }
    }

    private SignatureResponseBehavior AggregateLaneSignals()
    {
        var behavioralScore = GetLatestDoubleSignal(_behavioralLane.ScopedScoreKey);
        var spectralScore = GetLatestDoubleSignal(_spectralLane.ScopedScoreKey);
        var reputationScore = GetLatestDoubleSignal(_reputationLane.ScopedScoreKey);

        // Weighted average (configurable in future)
        var combinedScore = behavioralScore * 0.4 + spectralScore * 0.3 + reputationScore * 0.3;

        return new SignatureResponseBehavior
        {
            Signature = _signature,
            Score = combinedScore,
            BehavioralScore = behavioralScore,
            SpectralScore = spectralScore,
            ReputationScore = reputationScore,
            WindowSize = _window.Count
        };
    }

    private double GetLatestDoubleSignal(string signalName, double defaultValue = 0.0)
    {
        // Query signals by name (ephemeral 1.6.8: SignalEvent has .Signal property)
        var events = _sink.Sense(evt => evt.Signal == signalName);
        var latest = events.OrderByDescending(e => e.Timestamp).FirstOrDefault();

        // The signal name itself contains the value when using pattern "lane.score"
        // We emit as: Raise("behavioral.score", "0.1234")
        // So we need to parse the Key property which contains the value
        if (latest != default && latest.Key != null && double.TryParse(latest.Key, out var value))
            return value;

        return defaultValue;
    }
}