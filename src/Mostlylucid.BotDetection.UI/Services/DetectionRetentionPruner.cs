using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Periodic cleanup of raw detection rows past the configured retention window.
///     The <c>dashboard_detections</c> table accumulates per-request data indefinitely;
///     without this, millions of stale rows degrade query performance and waste disk.
///     <para>
///     Runs on the <see cref="ScheduleCoordinator"/> <see cref="TickCadence.Tick1h"/>
///     (the "erase" dial of the forgetting curve — the compression fold, the other
///     dial, compresses rows before this point). Cheap, low-priority maintenance,
///     fired once per hour on the wall-clock boundary rather than on a private
///     <see cref="BackgroundService"/> timer loop (one coordinator emits ticks —
///     <c>feedback_no_background_services</c>).
///     </para>
/// </summary>
public sealed class DetectionRetentionPruner : IHostedService, IDisposable
{
    private readonly IScheduleCoordinator? _schedule;
    private readonly IDashboardEventStore _store;
    private readonly TimeSpan _retention;
    private readonly ILogger<DetectionRetentionPruner> _logger;
    private IDisposable? _tickSub;

    public DetectionRetentionPruner(
        IDashboardEventStore store,
        IOptions<StyloBotDashboardOptions> options,
        IScheduleCoordinator? schedule = null,
        ILogger<DetectionRetentionPruner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _retention = options.Value.DetectionRetention;
        _schedule = schedule;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Self-disable only when there's no tick fabric to subscribe to (viewer-mode host).
        if (_schedule is null) return Task.CompletedTask;

        try
        {
            _tickSub = _schedule.Subscribe(
                TickCadence.Tick1h,
                nameof(DetectionRetentionPruner),
                CostHint.Low,
                PruneTickAsync);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DetectionRetentionPruner: failed to subscribe to Tick1h.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tickSub?.Dispose();
        _tickSub = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _tickSub?.Dispose();

    private async Task PruneTickAsync(DateTimeOffset _, CancellationToken ct)
    {
        var cutoff = DateTime.UtcNow - _retention;
        // Audited store API (not raw SQL) — same delete the store's own startup
        // sweep runs. No-op on non-SQLite stores (their lane handles retention
        // separately).
        var pruned = await _store.PruneOldDetectionsAsync(cutoff, ct);

        if (pruned > 0)
            _logger?.LogInformation("DetectionRetentionPruner: pruned {Count} row(s) older than {Cutoff:yyyy-MM-dd}",
                pruned, cutoff);
    }
}
