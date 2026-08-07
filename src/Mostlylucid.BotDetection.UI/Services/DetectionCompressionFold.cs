using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     The compression fold's tick driver (temporal-store absorption). Each
///     <see cref="TickCadence.Tick5m"/> it asks the store to fold the aged
///     low-importance region — nulling per-request detail columns on old rows,
///     in importance order, in bounded batches — so the detections table stays
///     a single temporal store where resolution is lost by importance, not by
///     clock buckets.
///     <para>
///     Mirrors <c>DetectionRetentionPruner</c>'s shape: an <see cref="IHostedService"/>
///     that subscribes to the schedule coordinator and self-disables on a
///     viewer-mode host with no coordinator (<c>feedback_remote_mode_optional_di</c>).
///     The retention pruner erases at <c>DetectionRetention</c>; this fold
///     compresses before that point. Both are the two dials of the forgetting
///     curve (<see cref="TemporalStoreOptions"/>).
///     </para>
///     <para>
///     All knob values are a startup snapshot (FOSS hard rule: no runtime
///     options-reload — hot-reload is commercial-only). The fold is a no-op
///     unless <see cref="TemporalStoreOptions.CompressionEnabled"/> is set:
///     today's raw behavior is the default until a host opts in and validates.
///     Non-SQLite stores are skipped — their fold is their own lane's job.
///     </para>
/// </summary>
public sealed class DetectionCompressionFold : IHostedService, IDisposable
{
    private readonly IScheduleCoordinator? _schedule;
    private readonly IDashboardEventStore _store;
    private readonly TemporalStoreOptions _options;
    private readonly ILogger<DetectionCompressionFold>? _logger;
    private readonly TimeProvider _time;
    private IDisposable? _tickSub;

    public DetectionCompressionFold(
        IDashboardEventStore store,
        IOptions<StyloBotDashboardOptions> options,
        IScheduleCoordinator? schedule = null,
        ILogger<DetectionCompressionFold>? logger = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(options);
        _store = store;
        _options = options.Value.TemporalStore;
        _schedule = schedule;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Self-disable only when there's no tick fabric to subscribe to (viewer-mode host).
        if (_schedule is null) return Task.CompletedTask;

        try
        {
            _tickSub = _schedule.Subscribe(
                TickCadence.Tick5m,
                nameof(DetectionCompressionFold),
                CostHint.Low,
                FoldTickAsync);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "DetectionCompressionFold: failed to subscribe to Tick5m.");
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

    private async Task FoldTickAsync(DateTimeOffset _, CancellationToken ct)
    {
        if (!_options.CompressionEnabled) return;

        // Only the FOSS SQLite store folds here. Other stores (e.g. a fleet-scale
        // Postgres store) own their own compression lane; the default interface
        // implementation of FoldAgedDetectionsAsync returns 0, so this gate keeps
        // the fold from running a no-op pass against them.
        if (_store is not SqliteDashboardEventStore) return;

        var now = _time.GetUtcNow().UtcDateTime;
        var folded = await _store.FoldAgedDetectionsAsync(
            now - _options.HotWindow,
            now - _options.FullAbsorptionAge,
            _options.ImportanceFloor,
            _options.FoldBatchSize,
            ct);

        if (folded > 0)
        {
            _logger?.LogInformation(
                "DetectionCompressionFold: folded {Count} aged detection row(s) (hotCutoff={HotCutoff:O}, floor={Floor})",
                folded, now - _options.HotWindow, _options.ImportanceFloor);
        }
    }
}
