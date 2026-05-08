using Mostlylucid.BotDetection.Metrics;

namespace Mostlylucid.BotDetection.MonitoringPacks;

public sealed class AspNetMonitoringPack : IMonitoringPack
{
    private readonly bool _includeHostMeters;

    public AspNetMonitoringPack(bool includeHostMeters = false)
    {
        _includeHostMeters = includeHostMeters;
    }

    public string Id => "aspnet-monitoring";
    public string Name => "ASP.NET + StyloBot Metrics";
    public string Description => "StyloBot operational meters and optional ASP.NET host metrics";
    public string TabName => "System";
    public TimeSpan CollectionInterval => TimeSpan.FromSeconds(60);

    public IReadOnlyList<MeterCollectionGroup> MeterGroups => BuildGroups();

    private IReadOnlyList<MeterCollectionGroup> BuildGroups()
    {
        var groups = new List<MeterCollectionGroup>
        {
            new(BotDetectionMetrics.MeterName, new[]
            {
                new InstrumentCollectionSpec("botdetection.requests.total",     CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.bots.detected",      CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.humans.detected",    CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.errors.total",       CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.detection.duration", CollectedValueType.Histogram_P50),
                new InstrumentCollectionSpec("botdetection.detection.duration", CollectedValueType.Histogram_P95),
                new InstrumentCollectionSpec("botdetection.confidence.average", CollectedValueType.Gauge),
                new InstrumentCollectionSpec("botdetection.weightstore.cache.hits",   CollectedValueType.Counter),
                new InstrumentCollectionSpec("botdetection.weightstore.cache.misses", CollectedValueType.Counter),
            })
        };

        if (_includeHostMeters)
        {
            groups.Add(new("Microsoft.AspNetCore.Hosting", new[]
            {
                new InstrumentCollectionSpec("http.server.request.duration", CollectedValueType.Histogram_P50),
                new InstrumentCollectionSpec("http.server.request.duration", CollectedValueType.Histogram_P95),
                new InstrumentCollectionSpec("http.server.active_requests",  CollectedValueType.Gauge),
            }));

            groups.Add(new("System.Runtime", new[]
            {
                new InstrumentCollectionSpec("dotnet.gc.heap.total_allocated",  CollectedValueType.Counter),
                new InstrumentCollectionSpec("dotnet.process.cpu.time",         CollectedValueType.Counter),
                new InstrumentCollectionSpec("dotnet.thread_pool.thread.count", CollectedValueType.Gauge),
            }));
        }

        return groups;
    }
}
