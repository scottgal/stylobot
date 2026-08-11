using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     Disk persistence for the rendered-content caches (operator directive 2026-08-11):
///     the L1 shingle cache (rendered widget HTML) is snapshotted to a local file each
///     materializer cycle and restored on warm boot BEFORE the materializer refreshes —
///     so even a restart with the gateway/DB down serves the last-known-good rendered
///     widgets immediately.
///     <para>
///         Boot sequence: (1) restore the persisted shingles from disk (cache populated
///         instantly), (2) pages serve from the disk-loaded cache, (3) the tick
///         materializer refreshes in the background, (4) this service persists the fresh
///         snapshot each cycle. Self-disables when no <c>DiskCachePath</c> is configured.
///         Fault-isolated: a failed persist/restore never fails host boot.
///     </para>
/// </summary>
public sealed class DashboardCacheDiskPersistence : IHostedService, IDisposable
{
    private readonly DashboardWidgetShingleCache _shingles;
    private readonly IOptions<DashboardMaterializerOptions> _optionsAccessor;
    private readonly IScheduleCoordinator? _schedule;
    private readonly ILogger<DashboardCacheDiskPersistence>? _logger;
    private IDisposable? _tickSub;

    // Startup-snapshot only (FOSS hard rule: no runtime options-reload).
    private DashboardMaterializerOptions _options => _optionsAccessor.Value;

    public DashboardCacheDiskPersistence(
        DashboardWidgetShingleCache shingles,
        IOptions<DashboardMaterializerOptions> options,
        IScheduleCoordinator? schedule = null,
        ILogger<DashboardCacheDiskPersistence>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(shingles);
        ArgumentNullException.ThrowIfNull(options);
        _shingles = shingles;
        _optionsAccessor = options;
        _schedule = schedule;
        _logger = logger;
    }

    private string? CachePath => string.IsNullOrWhiteSpace(_options.DiskCachePath)
        ? null
        : _options.DiskCachePath;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        var path = CachePath;
        if (path is null) return Task.CompletedTask;

        // 1. Warm-boot load: restore the persisted shingles BEFORE the materializer
        //    refreshes, so the first widget reads serve the last-known-good HTML.
        try
        {
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var snapshot = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (snapshot is { Count: > 0 })
                {
                    _shingles.Restore(snapshot);
                    _logger?.LogInformation(
                        "DashboardCacheDiskPersistence: restored {Count} shingles from {Path}.",
                        snapshot.Count, path);
                }
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex,
                "DashboardCacheDiskPersistence: warm-boot restore failed — serving from a cold cache.");
        }

        // 2. Persist each materializer cycle (Tick1m — the same 60s refresh cadence the
        //    materializer's per-envelope warm uses). A persist failure is logged, never
        //    thrown (a broken disk must not disturb the tick pipeline).
        if (_schedule is not null)
        {
            try
            {
                _tickSub = _schedule.Subscribe(
                    TickCadence.Tick1m,
                    nameof(DashboardCacheDiskPersistence),
                    CostHint.Low,
                    (_, ct) => PersistAsync(path, ct));
            }
            catch (Exception ex)
            {
                _logger?.LogWarning(ex, "DashboardCacheDiskPersistence: failed to subscribe to Tick1m.");
            }
        }

        return Task.CompletedTask;
    }

    private async Task PersistAsync(string path, CancellationToken ct)
    {
        try
        {
            var snapshot = _shingles.Snapshot();
            if (snapshot.Count == 0) return; // nothing rendered yet — nothing to persist

            var json = JsonSerializer.Serialize(snapshot);
            // Atomic-ish write: temp file + rename so a crash mid-write never leaves a
            // truncated file that a warm boot would load as authoritative.
            var tmp = path + ".tmp";
            await File.WriteAllTextAsync(tmp, json, ct).ConfigureAwait(false);
            File.Move(tmp, path, overwrite: true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown — fine.
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "DashboardCacheDiskPersistence: persist failed (kept serving from memory).");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _tickSub?.Dispose();
        _tickSub = null;
        return Task.CompletedTask;
    }

    public void Dispose() => _tickSub?.Dispose();
}
