using System.Threading.Channels;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Events;

/// <summary>
///     Signal types emitted during bot detection.
///     Each signal represents a detection phase completing.
/// </summary>
public enum BotSignalType
{
    // Stage 0: Raw signals (no dependencies)
    UserAgentAnalyzed,
    HeadersAnalyzed,
    IpAnalyzed,
    ClientFingerprintReceived,

    // Stage 1: Behavioral (depends on raw signals)
    BehaviourSampled,

    // Stage 2: Meta-analysis (depends on stages 0+1)
    InconsistencyUpdated,
    VersionAgeAnalyzed,

    // Stage 3: Heuristic early (uses basic features, runs before AI)
    HeuristicEarlyCompleted,

    // Stage 4: AI/ML (can read all prior signals including early heuristic)
    AiClassificationCompleted,
    LlmClassificationCompleted,

    // Stage 5: Heuristic late (runs AFTER AI, consumes all evidence)
    HeuristicLateCompleted,

    // Detector completion signals (for consensus)
    DetectorComplete,

    // Final: Risk assessment consolidation (emitted when consensus reached)
    Finalising
}

/// <summary>
///     Completion status from a detector
/// </summary>
public enum DetectorCompletionStatus
{
    /// <summary>Detector finished successfully with a result</summary>
    Completed,

    /// <summary>Detector skipped (not enabled or not applicable)</summary>
    Skipped,

    /// <summary>Detector failed but detection can continue</summary>
    Failed,

    /// <summary>Detector wants to abort detection early (e.g., verified bot found)</summary>
    EarlyExit
}

/// <summary>
///     Listener that reacts to detection signals
/// </summary>
public interface IBotSignalListener
{
    /// <summary>
    ///     Handle a detection signal
    /// </summary>
    ValueTask OnSignalAsync(
        BotSignalType signal,
        DetectionContext context,
        CancellationToken ct = default);
}

/// <summary>
///     A signal message published to the bus
/// </summary>
public readonly record struct SignalMessage(
    BotSignalType Signal,
    string SourceDetector,
    DateTimeOffset Timestamp);

/// <summary>
///     Lightweight in-memory signal bus using System.Threading.Channels.
///     Thread-safe, allocation-light, supports async consumption.
///     Consensus-based finalisation: emits Finalising when all detectors report.
/// </summary>
public sealed class BotSignalBus : IAsyncDisposable
{
    private readonly Channel<SignalMessage> _channel;
    private readonly Dictionary<string, DetectorCompletion> _completions = new();
    private readonly TaskCompletionSource _consensusTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HashSet<string> _expectedDetectors = new();
    private readonly Dictionary<BotSignalType, List<IBotSignalListener>> _listeners = new();
    private readonly object _lock = new();
    private readonly List<SignalMessage> _signalHistory = new();
    private bool _earlyExit;

    public BotSignalBus()
    {
        // Unbounded channel - signals are small, won't accumulate much per-request
        _channel = Channel.CreateUnbounded<SignalMessage>(new UnboundedChannelOptions
        {
            SingleReader = true, // Only the orchestrator reads
            SingleWriter = false, // Multiple detectors can write
            AllowSynchronousContinuations = true // Fast path for sync completions
        });
    }

    /// <summary>
    ///     Check if consensus has been reached
    /// </summary>
    public bool HasConsensus
    {
        get
        {
            lock (_lock)
            {
                return CheckConsensusLocked();
            }
        }
    }

    /// <summary>
    ///     Check if early exit was requested
    /// </summary>
    public bool ShouldEarlyExit
    {
        get
        {
            lock (_lock)
            {
                return _earlyExit;
            }
        }
    }

    /// <summary>
    ///     Get completion status for all detectors
    /// </summary>
    public IReadOnlyDictionary<string, DetectorCompletion> Completions
    {
        get
        {
            lock (_lock)
            {
                return new Dictionary<string, DetectorCompletion>(_completions);
            }
        }
    }

    /// <summary>
    ///     Get which detectors haven't reported yet
    /// </summary>
    public IEnumerable<string> PendingDetectors
    {
        get
        {
            lock (_lock)
            {
                return _expectedDetectors.Where(d => !_completions.ContainsKey(d)).ToList();
            }
        }
    }

    /// <summary>
    ///     Get the history of signals published
    /// </summary>
    public IReadOnlyList<SignalMessage> SignalHistory
    {
        get
        {
            lock (_lock)
            {
                return _signalHistory.ToList();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        Complete();
        _consensusTcs.TrySetCanceled();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    ///     Register expected detectors for consensus tracking
    /// </summary>
    public void ExpectDetectors(IEnumerable<string> detectorNames)
    {
        lock (_lock)
        {
            foreach (var name in detectorNames)
                _expectedDetectors.Add(name);
        }
    }

    /// <summary>
    ///     Register a listener for a specific signal
    /// </summary>
    public void Register(BotSignalType signal, IBotSignalListener listener)
    {
        lock (_lock)
        {
            if (!_listeners.TryGetValue(signal, out var list))
            {
                list = new List<IBotSignalListener>();
                _listeners[signal] = list;
            }

            list.Add(listener);
        }
    }

    /// <summary>
    ///     Register a listener for multiple signals
    /// </summary>
    public void Register(IEnumerable<BotSignalType> signals, IBotSignalListener listener)
    {
        foreach (var signal in signals)
            Register(signal, listener);
    }

    /// <summary>
    ///     Publish a signal to the channel (non-blocking)
    /// </summary>
    public bool TryPublish(BotSignalType signal, string sourceDetector)
    {
        var msg = new SignalMessage(signal, sourceDetector, DateTimeOffset.UtcNow);

        lock (_lock)
        {
            _signalHistory.Add(msg);
        }

        return _channel.Writer.TryWrite(msg);
    }

    /// <summary>
    ///     Report detector completion for consensus tracking
    /// </summary>
    public void ReportCompletion(string detectorName, DetectorCompletionStatus status, string? reason = null)
    {
        bool reachedConsensus;

        lock (_lock)
        {
            _completions[detectorName] = new DetectorCompletion
            {
                DetectorName = detectorName,
                Status = status,
                Reason = reason,
                Timestamp = DateTimeOffset.UtcNow
            };

            if (status == DetectorCompletionStatus.EarlyExit)
                _earlyExit = true;

            reachedConsensus = CheckConsensusLocked();
        }

        if (reachedConsensus) _consensusTcs.TrySetResult();
    }

    private bool CheckConsensusLocked()
    {
        return _earlyExit || _expectedDetectors.All(d => _completions.ContainsKey(d));
    }

    /// <summary>
    ///     Wait for consensus (all detectors reported or early exit)
    /// </summary>
    public Task WaitForConsensusAsync(CancellationToken ct = default)
    {
        if (HasConsensus)
            return Task.CompletedTask;

        return _consensusTcs.Task.WaitAsync(ct);
    }

    /// <summary>
    ///     Wait for consensus with timeout
    /// </summary>
    public async Task<bool> WaitForConsensusAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        if (HasConsensus)
            return true;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await _consensusTcs.Task.WaitAsync(cts.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return HasConsensus; // Check one more time
        }
    }

    /// <summary>
    ///     Process all signals in the channel, invoking listeners.
    ///     Call this after all detectors have published their signals.
    /// </summary>
    public async ValueTask ProcessSignalsAsync(DetectionContext context, CancellationToken ct = default)
    {
        _channel.Writer.TryComplete();

        await foreach (var msg in _channel.Reader.ReadAllAsync(ct))
        {
            List<IBotSignalListener>? listeners;
            lock (_lock)
            {
                _listeners.TryGetValue(msg.Signal, out listeners);
            }

            if (listeners == null)
                continue;

            // Sequential for predictable behavior
            foreach (var listener in listeners) await listener.OnSignalAsync(msg.Signal, context, ct);
        }
    }

    /// <summary>
    ///     Consume signals as they arrive (async enumerable)
    /// </summary>
    public IAsyncEnumerable<SignalMessage> ReadAllAsync(CancellationToken ct = default)
    {
        return _channel.Reader.ReadAllAsync(ct);
    }

    /// <summary>
    ///     Check if a signal has been published
    /// </summary>
    public bool HasSignal(BotSignalType signal)
    {
        lock (_lock)
        {
            return _signalHistory.Any(h => h.Signal == signal);
        }
    }

    /// <summary>
    ///     Complete the channel (no more signals will be published)
    /// </summary>
    public void Complete()
    {
        _channel.Writer.TryComplete();
    }
}

/// <summary>
///     Record of a detector's completion
/// </summary>
public class DetectorCompletion
{
    public required string DetectorName { get; init; }
    public DetectorCompletionStatus Status { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset Timestamp { get; init; }
}

/// <summary>
///     Factory for creating signal buses with pre-registered listeners
/// </summary>
public interface IBotSignalBusFactory
{
    /// <summary>
    ///     Create a new signal bus with all listeners registered
    /// </summary>
    BotSignalBus Create();
}

/// <summary>
///     Default factory that registers all IBotSignalListener implementations
/// </summary>
public class BotSignalBusFactory : IBotSignalBusFactory
{
    private readonly IEnumerable<IBotSignalListener> _listeners;

    public BotSignalBusFactory(IEnumerable<IBotSignalListener> listeners)
    {
        _listeners = listeners;
    }

    public BotSignalBus Create()
    {
        var bus = new BotSignalBus();

        foreach (var listener in _listeners)
            // Each listener declares what signals it listens to via attribute or interface
            if (listener is ISignalSubscriber subscriber)
                bus.Register(subscriber.SubscribedSignals, listener);

        return bus;
    }
}

/// <summary>
///     Interface for listeners to declare their signal subscriptions
/// </summary>
public interface ISignalSubscriber
{
    /// <summary>
    ///     Signals this listener wants to receive
    /// </summary>
    IEnumerable<BotSignalType> SubscribedSignals { get; }
}