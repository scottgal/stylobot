using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration.Lanes;
using Mostlylucid.BotDetection.Orchestration.Signals;
using Mostlylucid.Ephemeral;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Signature-level response coordinator with TIGHT coupling to its own sink.
///     Manages cross-request response state and lanes for a single signature.
///     Uses WaveAtom for parallel lane execution.
/// </summary>
public sealed class SignatureResponseCoordinator : IAsyncDisposable
{
    // Lanes (share the coordinator's sink)
    private readonly IReadOnlyList<IAnalysisLane> _lanes;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger _logger;
    private readonly string _signature;
    private readonly SignalSink _sink; // OWNED by this coordinator (TIGHT)
    private readonly LinkedList<OperationCompleteSignal> _window;

    public SignatureResponseCoordinator(string signature, ILogger logger)
    {
        _signature = signature;
        _logger = logger;

        // Create TIGHT sink (owned by this coordinator)
        _sink = new SignalSink(
            10000,
            TimeSpan.FromHours(24));

        _window = new LinkedList<OperationCompleteSignal>();

        // Initialize lanes - all share this coordinator's sink
        _lanes = new List<IAnalysisLane>
        {
            new BehavioralLane(_sink),
            new SpectralLane(_sink),
            new ReputationLane(_sink)
        };

        _logger.LogDebug("SignatureResponseCoordinator created for {Signature} with {LaneCount} lanes",
            signature, _lanes.Count);
    }

    public async ValueTask DisposeAsync()
    {
        // SignalSink disposal handled by GC (no Dispose method in v1.6.8)
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

            // If honeypot or very high risk, update heuristic immediately
            if (signal.Honeypot || signal.Risk > 0.8)
                _logger.LogWarning(
                    "Early high-risk signal for {Signature}: risk={Risk:F2}, honeypot={Honeypot}",
                    _signature, signal.Risk, signal.Honeypot);
            // TODO: Immediate heuristic feedback
            // heuristicStore.UpdateAsync(_signature, signal.Risk);
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

    /// <summary>
    ///     Aggregate signals from lanes into signature behavior.
    /// </summary>
    private SignatureResponseBehavior AggregateLaneSignals()
    {
        var behavioralScore = GetLatestDoubleSignal("behavioral.score");
        var spectralScore = GetLatestDoubleSignal("spectral.score");
        var reputationScore = GetLatestDoubleSignal("reputation.score");

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