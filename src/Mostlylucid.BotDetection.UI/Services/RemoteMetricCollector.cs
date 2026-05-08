using System.Net.Http.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.UI.Services;

public sealed class RemoteMetricCollector : BackgroundService
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly string _gatewayUrl;
    private readonly TimeSpan _pollInterval;
    private readonly IMetricSnapshotStore _store;
    private readonly ILogger<RemoteMetricCollector> _logger;

    public RemoteMetricCollector(
        IHttpClientFactory httpFactory,
        string gatewayUrl,
        TimeSpan pollInterval,
        IMetricSnapshotStore store,
        ILogger<RemoteMetricCollector> logger)
    {
        _httpFactory = httpFactory;
        _gatewayUrl = gatewayUrl;
        _pollInterval = pollInterval;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("RemoteMetricCollector started, polling {Url} every {Interval}s",
            _gatewayUrl, _pollInterval.TotalSeconds);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(_pollInterval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                using var client = _httpFactory.CreateClient("sb-metrics");
                var dtos = await client.GetFromJsonAsync<MetricSnapshotDto[]>(_gatewayUrl, stoppingToken);
                if (dtos == null || dtos.Length == 0) continue;

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

                await _store.WriteSnapshotsAsync(snapshots, stoppingToken);
                _logger.LogDebug("RemoteMetricCollector: wrote {Count} snapshots from gateway", dtos.Length);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "RemoteMetricCollector: failed to poll gateway metrics");
            }
        }
    }
}
