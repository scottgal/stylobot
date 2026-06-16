using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Wraps the learning event bus with a tightly bounded channel when HP mode is enabled.
///     Fast path cost: one lock-free TryWrite (~20ns). Handler invocation is background-only.
///     When HP mode is off, all calls pass through directly to the inner bus with zero overhead.
///
///     Channel sizing: the inner <see cref="LearningEventBus"/> already uses a 10,000-entry channel.
///     HP mode adds a second, smaller front-end channel (<see cref="SelfMaintenanceOptions.LearningQueueDepth"/>,
///     default 1,000) whose sole purpose is to make TryPublish return in ~20ns on the hot path.
///     The background consumer drains this front-end channel and forwards to the inner bus.
///     Events are dropped (oldest first) only when the front-end channel is full, keeping
///     memory bounded on low-resource deployments.
///
///     <para>
///         <b>Wave 2 architectural-drift remediation (Category B).</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> ran an unbounded <c>await foreach</c> on the
///         front-end channel and forwarded each entry to the inner bus. Now
///         subscribes to <see cref="TickCadence.Tick1s"/> via
///         <see cref="IScheduleCoordinator"/>; each tick drains whatever
///         landed since the last tick via
///         <see cref="ChannelReader{T}.TryRead"/> and forwards to the inner
///         bus. The HP-mode latency budget is "as fast as possible without
///         blocking the request thread"; a 1-second tick floor is the
///         shortest cadence the coordinator emits and matches that intent
///         while the existing front-end channel absorbs bursts.
///     </para>
///     <para>
///         When HP mode is OFF the tick handler is registered but early-
///         returns each tick (since the front-end channel never sees writes
///         in non-HP mode). This keeps the subscription wiring uniform
///         across modes; the cost is one TryRead-against-empty per tick,
///         which is sub-microsecond.
///     </para>
/// </summary>
public sealed class BoundedChannelLearningBus : ILearningEventBus, IDisposable
{
    private readonly ILearningEventBus _inner;
    private readonly Channel<LearningEvent> _channel;
    private readonly ILogger<BoundedChannelLearningBus> _logger;
    private readonly bool _enabled;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public BoundedChannelLearningBus(
        LearningEventBus inner,
        IOptions<BotDetectionOptions> options,
        ILogger<BoundedChannelLearningBus> logger,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _inner = inner;
        _logger = logger;
        var sm = options.Value.SelfMaintenance;
        _enabled = sm.HighPerformanceMode;

        _channel = Channel.CreateBounded<LearningEvent>(new BoundedChannelOptions(sm.LearningQueueDepth)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

        // Optional so existing direct-construction tests + ASP.NET fixtures
        // that don't register a coordinator keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1s,
                "BoundedChannelLearningBus",
                CostHint.High,
                OnTickAsync);
        }
    }

    /// <inheritdoc />
    /// <remarks>
    ///     HP mode on: lock-free TryWrite (~20ns) then return. Returns false only if the writer
    ///     has been completed (shutdown). Dropped-due-to-full events return true from the
    ///     channel perspective; the drop is handled silently by BoundedChannelFullMode.DropOldest.
    ///
    ///     HP mode off: delegates directly to the inner bus (zero added overhead).
    /// </remarks>
    public bool TryPublish(LearningEvent evt)
    {
        if (!_enabled)
            return _inner.TryPublish(evt);

        if (!_channel.Writer.TryWrite(evt))
        {
            // Writer has been completed (shutdown path), not a queue-full drop.
            _logger.LogDebug("HP learning channel completed; dropping {EventType} event", evt.Type);
            return false;
        }

        return true;
    }

    /// <summary>
    ///     Exposes the inner bus's reader so that <see cref="LearningBackgroundService"/>
    ///     still drains the main channel. The front-end channel is an implementation detail
    ///     of the HP fast path; downstream consumers always see the inner bus.
    /// </summary>
    public ChannelReader<LearningEvent> Reader => _inner.Reader;

    /// <inheritdoc />
    public ChannelReader<LearningEvent> Subscribe(string subscriberName, int capacity = 10_000)
        => _inner.Subscribe(subscriberName, capacity);

    /// <inheritdoc />
    public void Complete()
    {
        _channel.Writer.TryComplete();
        _inner.Complete();
    }

    /// <summary>Visible depth for tests + diagnostics.</summary>
    public int FrontEndQueueDepth => _channel.Reader.Count;

    /// <summary>
    ///     ScheduleCoordinator tick handler. When HP mode is on, drain
    ///     whatever events landed on the front-end channel since the last
    ///     tick via <see cref="ChannelReader{T}.TryRead"/> and forward each
    ///     to the inner bus. When HP mode is off the front-end channel
    ///     never sees writes, so the TryRead loop exits immediately.
    /// </summary>
    public Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return Task.CompletedTask;

        while (_channel.Reader.TryRead(out var evt))
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                _inner.TryPublish(evt);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "HP learning consumer error for {EventType}", evt.Type);
            }
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
        try { _channel.Writer.TryComplete(); }
        catch { /* already torn down */ }
    }
}