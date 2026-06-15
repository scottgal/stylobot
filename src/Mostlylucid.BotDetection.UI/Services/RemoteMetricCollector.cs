using System.Net.Http.Json;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Polls a remote gateway's metrics endpoint and writes the snapshots into
///     the local <see cref="IMetricSnapshotStore"/>. Used by dashboard hosts
///     running in remote-mode (the dashboard renders a gateway it does not
///     own).
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(_pollInterval)</c> loop; now subscribes to
///         <see cref="TickCadence.Tick10s"/> on
///         <see cref="IScheduleCoordinator"/> and gates the poll on
///         "last-poll older than the configured interval". The HTTP call,
///         store write, and remote-mode contract are unchanged. See
///         <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed class RemoteMetricCollector : IDisposable
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _gatewayUrl;
    private readonly TimeSpan _pollInterval;
    private readonly IMetricSnapshotStore _store;
    private readonly ILogger<RemoteMetricCollector> _logger;
    private readonly IDisposable? _subscription;
    private DateTime _lastPollUtc = DateTime.MinValue;
    private bool _firstTickAnnounced;
    private int _disposed;

    public RemoteMetricCollector(
        IHttpClientFactory httpFactory,
        string gatewayUrl,
        TimeSpan pollInterval,
        IMetricSnapshotStore store,
        ILogger<RemoteMetricCollector> logger,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _httpFactory = httpFactory;
        _gatewayUrl = gatewayUrl;
        _pollInterval = pollInterval;
        _store = store;
        _logger = logger;

        // Optional so existing direct-construction tests keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "RemoteMetricCollector",
                CostHint.Low,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Fires every Tick10s; gates the
    ///     poll on "last-poll older than the configured interval" so the
    ///     remote fetch cadence honours the constructor-supplied poll period
    ///     even when the tick cadence is finer. Public so tests can drive a
    ///     single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        if (!_firstTickAnnounced)
        {
            _logger.LogInformation(
                "RemoteMetricCollector started, polling {Url} every {Interval}s",
                _gatewayUrl, _pollInterval.TotalSeconds);
            _firstTickAnnounced = true;
        }

        if (_lastPollUtc != DateTime.MinValue &&
            now.UtcDateTime - _lastPollUtc < _pollInterval)
        {
            return; // Not yet due.
        }

        _lastPollUtc = now.UtcDateTime;

        try
        {
            using var client = _httpFactory.CreateClient("sb-metrics");
            var dtos = await client.GetFromJsonAsync<MetricSnapshotDto[]>(_gatewayUrl, ct);
            if (dtos == null || dtos.Length == 0) return;

            var snapshots = dtos.Select(d => new MetricSnapshot
            {
                BucketTime = d.BucketTime.TruncateToMinute(),
                PackId = d.PackId,
                MeterName = d.MeterName,
                Instrument = d.Instrument,
                Tags = d.Tags,
                Value = d.Value,
                ValueType = d.ValueType
            });

            await _store.WriteSnapshotsAsync(snapshots, ct);
            _logger.LogDebug("RemoteMetricCollector: wrote {Count} snapshots from gateway", dtos.Length);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RemoteMetricCollector: failed to poll gateway metrics");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}