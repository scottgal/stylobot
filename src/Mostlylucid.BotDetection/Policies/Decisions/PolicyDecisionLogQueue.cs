using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Policies.Decisions;

/// <summary>
///     Default <see cref="IPolicyDecisionLogQueue"/>: a bounded channel that
///     the <see cref="Policies.Dispatch.PolicyActionDispatcher"/> writes to
///     on the request hot path, drained into the underlying
///     <see cref="IPolicyDecisionLog"/> on
///     <see cref="TickCadence.Tick1s"/>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Wave 4 architectural-drift remediation:</b> the dispatcher used
///         to <c>await _decisionLog.AppendAsync(...)</c> inline. Even when
///         the implementation was a fast channel-TryWrite the await cost the
///         request thread a state-machine traversal AND coupled per-request
///         latency to whatever store backed the log. The queue takes the
///         write entirely off the hot path; the drainer flushes once per
///         second.
///     </para>
///     <para>
///         <b>Pattern reuse:</b> mirrors
///         <see cref="Telemetry.PolicyEffectivenessCache"/> (bounded channel
///         + drainer + DropOldest). We do NOT introduce a parallel
///         write-behind primitive.
///     </para>
/// </remarks>
public sealed class PolicyDecisionLogQueue : IPolicyDecisionLogQueue, IDisposable
{
    /// <summary>
    ///     Default bounded-channel capacity. The dispatcher emits at most one
    ///     row per matched rule per request; 8k accommodates a 1Hz drain
    ///     against ~8 thousand QPS without back-pressure even when every
    ///     request matches multiple rules.
    /// </summary>
    public const int DefaultChannelCapacity = 8192;

    private readonly IPolicyDecisionLog _log;
    private readonly ILogger<PolicyDecisionLogQueue> _logger;
    private readonly Channel<PolicyDecision> _channel;
    private readonly IDisposable _tickSubscription;
    private long _droppedCount;
    private int _disposed;

    /// <summary>
    ///     Test-friendly constructor with explicit capacity. Production
    ///     callers use the DI constructor below which pulls the default
    ///     capacity.
    /// </summary>
    public PolicyDecisionLogQueue(
        IPolicyDecisionLog log,
        IScheduleCoordinator coordinator,
        int channelCapacity = DefaultChannelCapacity,
        ILogger<PolicyDecisionLogQueue>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(coordinator);
        if (channelCapacity <= 0) channelCapacity = DefaultChannelCapacity;

        _log = log;
        _logger = logger ?? NullLogger<PolicyDecisionLogQueue>.Instance;

        _channel = Channel.CreateBounded<PolicyDecision>(new BoundedChannelOptions(channelCapacity)
        {
            // DropOldest matches PolicyEffectivenessCache: under pressure we
            // discard the stalest row, not the newest -- the newest is what
            // the operator just saw in the dashboard.
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

        _tickSubscription = coordinator.Subscribe(
            TickCadence.Tick1s,
            "PolicyDecisionLogQueue.Drain",
            CostHint.Low,
            (_, ct) => FlushAsync(ct));
    }

    /// <inheritdoc />
    public long DroppedCount => Interlocked.Read(ref _droppedCount);

    /// <inheritdoc />
    public void Enqueue(PolicyDecision decision)
    {
        if (_disposed != 0) return;
        if (!_channel.Writer.TryWrite(decision))
        {
            // DropOldest channels only fail TryWrite when the writer is completed
            // (e.g. during disposal). Bump the drop counter so we don't silently
            // lose track.
            Interlocked.Increment(ref _droppedCount);
        }
    }

    /// <inheritdoc />
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_disposed != 0) return;

        while (_channel.Reader.TryRead(out var decision))
        {
            try
            {
                await _log.AppendAsync(decision, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "PolicyDecisionLogQueue: durable append failed; row dropped");
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _tickSubscription.Dispose(); }
        catch { /* coordinator already torn down */ }
        _channel.Writer.TryComplete();
    }
}
