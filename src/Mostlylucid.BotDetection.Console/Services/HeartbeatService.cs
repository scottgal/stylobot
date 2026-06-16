using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Heartbeat service to detect silent failures -- logs every 5 minutes.
///     <para>
///         <b>Wave 2 architectural-drift remediation pilot.</b> Was a
///         <see cref="BackgroundService"/> with a <c>Task.Delay(5min)</c>
///         loop; now subscribes to <see cref="TickCadence.Tick5m"/> on
///         <see cref="IScheduleCoordinator"/>. See
///         <c>feedback_no_background_services</c>.
///     </para>
///     <para>
///         No state moved -- this service was already stateless aside from a
///         per-process counter -- but the lifecycle hook changed from
///         <c>AddHostedService</c> to a plain singleton brought up by the
///         console's bootstrap shim.
///     </para>
/// </summary>
public sealed class HeartbeatService : IDisposable
{
    private readonly ILogger<HeartbeatService> _logger;
    private readonly IDisposable _subscription;
    private int _heartbeatCount;
    private int _disposed;

    public HeartbeatService(ILogger<HeartbeatService> logger, IScheduleCoordinator coordinator)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _logger = logger;

        _logger.LogInformation(
            "Heartbeat service started - will log every 5 minutes to detect silent failures");

        _subscription = coordinator.Subscribe(
            TickCadence.Tick5m,
            "HeartbeatService",
            CostHint.Low,
            OnTickAsync);
    }

    /// <summary>
    ///     The coordinator's tick handler. Drives the heartbeat-emit body that
    ///     used to live in <c>ExecuteAsync</c>'s loop. Public so tests can
    ///     drive a single beat deterministically without going through the
    ///     coordinator.
    /// </summary>
    public Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return Task.CompletedTask;

        _heartbeatCount++;
        _logger.LogInformation("💓 HEARTBEAT #{Count} - Gateway still running (Uptime: {Uptime} minutes)",
            _heartbeatCount,
            _heartbeatCount * 5);

        // Log memory usage to detect memory leaks
        var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
        _logger.LogInformation("   Memory: {MemoryMB} MB, GC Gen0: {Gen0}, Gen1: {Gen1}, Gen2: {Gen2}",
            memoryMB,
            GC.CollectionCount(0),
            GC.CollectionCount(1),
            GC.CollectionCount(2));

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}