using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Mostlylucid.BotDetection.Events;

/// <summary>
///     Event-driven signal bus for detection pipeline.
///     Detectors publish signals; subscribers react when their required signals arrive.
/// </summary>
public interface IDetectionSignalBus
{
    /// <summary>
    ///     Publish a signal to the bus
    /// </summary>
    void Publish(DetectionSignal signal);

    /// <summary>
    ///     Subscribe to signals matching specific keys
    /// </summary>
    IAsyncEnumerable<DetectionSignal> Subscribe(
        IReadOnlySet<string> requiredSignalKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Wait until all required signals are available, then return them
    /// </summary>
    Task<IReadOnlyDictionary<string, DetectionSignal>> WaitForSignalsAsync(
        IReadOnlySet<string> requiredSignalKeys,
        TimeSpan timeout,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Get all signals published so far
    /// </summary>
    IReadOnlyDictionary<string, DetectionSignal> GetAllSignals();

    /// <summary>
    ///     Signal that detection is complete
    /// </summary>
    void Complete();
}

/// <summary>
///     A signal emitted by a detector
/// </summary>
public class DetectionSignal
{
    /// <summary>
    ///     Unique key for this signal (e.g., "ua.is_bot", "ip.is_datacenter")
    /// </summary>
    public required string Key { get; init; }

    /// <summary>
    ///     The signal value
    /// </summary>
    public required object Value { get; init; }

    /// <summary>
    ///     Which detector emitted this signal
    /// </summary>
    public required string SourceDetector { get; init; }

    /// <summary>
    ///     Confidence in this signal (0.0 to 1.0)
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    ///     Timestamp when signal was emitted
    /// </summary>
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    ///     Get typed value
    /// </summary>
    public T? GetValue<T>()
    {
        return Value is T typed ? typed : default;
    }
}

/// <summary>
///     Channel-based implementation of the signal bus
/// </summary>
public class DetectionSignalBus : IDetectionSignalBus
{
    private readonly Channel<DetectionSignal> _channel;
    private readonly TaskCompletionSource _completionSource = new();
    private readonly object _lock = new();
    private readonly Dictionary<string, DetectionSignal> _signals = new();
    private bool _completed;

    public DetectionSignalBus()
    {
        _channel = Channel.CreateUnbounded<DetectionSignal>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });
    }

    public void Publish(DetectionSignal signal)
    {
        lock (_lock)
        {
            if (_completed) return;
            _signals[signal.Key] = signal;
        }

        _channel.Writer.TryWrite(signal);
    }

    public async IAsyncEnumerable<DetectionSignal> Subscribe(
        IReadOnlySet<string> requiredSignalKeys,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var signal in _channel.Reader.ReadAllAsync(cancellationToken))
            if (requiredSignalKeys.Contains(signal.Key))
                yield return signal;
    }

    public async Task<IReadOnlyDictionary<string, DetectionSignal>> WaitForSignalsAsync(
        IReadOnlySet<string> requiredSignalKeys,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var received = new Dictionary<string, DetectionSignal>();

        // First check what we already have
        lock (_lock)
        {
            foreach (var key in requiredSignalKeys)
                if (_signals.TryGetValue(key, out var signal))
                    received[key] = signal;
        }

        // If we have all, return immediately
        if (received.Count == requiredSignalKeys.Count)
            return received;

        // Otherwise wait for remaining signals
        try
        {
            await foreach (var signal in _channel.Reader.ReadAllAsync(cts.Token))
                if (requiredSignalKeys.Contains(signal.Key))
                {
                    received[signal.Key] = signal;

                    if (received.Count == requiredSignalKeys.Count)
                        return received;
                }
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested &&
                                                 !cancellationToken.IsCancellationRequested)
        {
            // Timeout - return what we have
        }

        return received;
    }

    public IReadOnlyDictionary<string, DetectionSignal> GetAllSignals()
    {
        lock (_lock)
        {
            return new Dictionary<string, DetectionSignal>(_signals);
        }
    }

    public void Complete()
    {
        lock (_lock)
        {
            if (_completed) return;
            _completed = true;
        }

        _channel.Writer.TryComplete();
        _completionSource.TrySetResult();
    }
}