using System.Threading.Channels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Models;

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
/// </summary>
public sealed class BoundedChannelLearningBus : BackgroundService, ILearningEventBus
{
    private readonly ILearningEventBus _inner;
    private readonly Channel<LearningEvent> _channel;
    private readonly ILogger<BoundedChannelLearningBus> _logger;
    private readonly bool _enabled;

    public BoundedChannelLearningBus(
        LearningEventBus inner,
        IOptions<BotDetectionOptions> options,
        ILogger<BoundedChannelLearningBus> logger)
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

    /// <summary>
    ///     Background loop: drains the front-end channel and forwards each event to the
    ///     inner bus. Only runs when HP mode is on; returns immediately otherwise.
    /// </summary>
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled) return;

        _logger.LogInformation("HP learning bus consumer started (queue depth: {Depth})",
            _channel.Reader.Count);

        try
        {
            await foreach (var evt in _channel.Reader.ReadAllAsync(stoppingToken))
            {
                try
                {
                    _inner.TryPublish(evt);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "HP learning consumer error for {EventType}", evt.Type);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal shutdown
        }

        _logger.LogInformation("HP learning bus consumer stopped");
    }
}
