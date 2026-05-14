using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.MonitoringPacks;

public sealed class MeterListenerService : BackgroundService, IDisposable
{
    private readonly Dictionary<string, IMonitoringPack> _packs;
    private readonly IMetricSnapshotStoreAccessor? _storeAccessor;
    private readonly ILogger<MeterListenerService> _logger;
    private readonly IPackRuntimeController? _runtimeController;
    private readonly object _packsLock = new();
    private MeterListener? _listener;

    private readonly ConcurrentDictionary<string, InstrumentState> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, List<double>> _histograms = new();
    private readonly object _histLock = new();

    private readonly IMetricSnapshotStore? _directStore;

    public MeterListenerService(
        IEnumerable<IMonitoringPack> packs,
        IMetricSnapshotStore store,
        ILogger<MeterListenerService> logger,
        IPackRuntimeController? runtimeController = null)
    {
        _packs = packs.ToDictionary(p => p.Id);
        _directStore = store;
        _logger = logger;
        _runtimeController = runtimeController;
        SubscribeRuntimeController();
    }

    public MeterListenerService(
        IEnumerable<IMonitoringPack> packs,
        IMetricSnapshotStoreAccessor storeAccessor,
        ILogger<MeterListenerService> logger,
        IPackRuntimeController? runtimeController = null)
    {
        _packs = packs.ToDictionary(p => p.Id);
        _storeAccessor = storeAccessor;
        _logger = logger;
        _runtimeController = runtimeController;
        SubscribeRuntimeController();
    }

    private void SubscribeRuntimeController()
    {
        if (_runtimeController is null) return;
        _runtimeController.PackChanged += OnPackChanged;
    }

    private void OnPackChanged(object? sender, PackChangedEventArgs e)
    {
        lock (_packsLock)
        {
            _packs[e.Pack.Id] = e.Pack;
        }
        RebuildListener();
        _logger.LogInformation(
            "MeterListener rebuilt after pack '{PackId}' was hot-reloaded", e.Pack.Id);
    }

    private void RebuildListener()
    {
        _listener?.Dispose();
        _listener = null;
        StartListening();
    }

    public void StartListening()
    {
        _listener = new MeterListener();

        _listener.InstrumentPublished = (instrument, listener) =>
        {
            foreach (var pack in SnapshotPacks())
            foreach (var group in pack.MeterGroups)
            {
                if (!string.Equals(instrument.Meter.Name, group.MeterName, StringComparison.OrdinalIgnoreCase))
                    continue;
                foreach (var spec in group.Instruments)
                {
                    if (!string.Equals(instrument.Name, spec.InstrumentName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    listener.EnableMeasurementEvents(instrument);
                }
            }
        };

        _listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            ProcessMeasurement(instrument, (double)measurement));
        _listener.SetMeasurementEventCallback<double>((instrument, measurement, _, _) =>
            ProcessMeasurement(instrument, measurement));
        _listener.SetMeasurementEventCallback<int>((instrument, measurement, _, _) =>
            ProcessMeasurement(instrument, (double)measurement));
        _listener.Start();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        StartListening();
        _logger.LogInformation("MeterListenerService started with {Count} pack(s)", _packs.Count);

        var packs = SnapshotPacks();
        var interval = packs.Count > 0
            ? packs.Min(p => p.CollectionInterval)
            : TimeSpan.FromSeconds(60);

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await Task.Delay(interval, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try
            {
                var store = _directStore ?? _storeAccessor!.GetStore();
                var snapshots = await FlushSnapshotsAsync(stoppingToken);
                if (snapshots.Count > 0)
                    await store.WriteSnapshotsAsync(snapshots, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush metric snapshots");
            }
        }
    }

    public async Task<List<MetricSnapshot>> FlushSnapshotsAsync(CancellationToken ct)
    {
        _listener?.RecordObservableInstruments();

        var bucket = DateTime.UtcNow.TruncateToMinute();
        var results = new List<MetricSnapshot>();

        foreach (var (key, state) in _counters)
        {
            var delta = Interlocked.Exchange(ref state.Total, 0);
            if (delta == 0) continue;
            var (packId, instrument) = ParseKey(key);
            results.Add(new MetricSnapshot
            {
                BucketTime = bucket,
                PackId = packId,
                MeterName = GetMeterName(packId, instrument),
                Instrument = instrument,
                Value = delta,
                ValueType = "rate"
            });
        }

        foreach (var (key, value) in _gauges)
        {
            var (packId, instrument) = ParseKey(key);
            results.Add(new MetricSnapshot
            {
                BucketTime = bucket,
                PackId = packId,
                MeterName = GetMeterName(packId, instrument),
                Instrument = instrument,
                Value = value,
                ValueType = "gauge"
            });
        }

        lock (_histLock)
        {
            foreach (var (key, observations) in _histograms)
            {
                if (observations.Count == 0) continue;
                var sorted = observations.OrderBy(v => v).ToList();
                observations.Clear();

                var (packId, rawKey) = ParseKey(key);
                var parts = rawKey.Split('|');
                var instrument = parts[0];
                var vtype = parts.Length > 1 ? parts[1] : "p50";

                var pct = vtype switch { "p95" => 0.95, "p99" => 0.99, _ => 0.50 };
                var idx = Math.Clamp((int)Math.Ceiling(pct * sorted.Count) - 1, 0, sorted.Count - 1);

                results.Add(new MetricSnapshot
                {
                    BucketTime = bucket,
                    PackId = packId,
                    MeterName = GetMeterName(packId, instrument),
                    Instrument = instrument,
                    Value = sorted[idx],
                    ValueType = vtype
                });
            }
        }

        return results;
    }

    private void ProcessMeasurement(Instrument instrument, double value)
    {
        foreach (var pack in SnapshotPacks())
        foreach (var group in pack.MeterGroups)
        {
            if (!string.Equals(instrument.Meter.Name, group.MeterName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var spec in group.Instruments)
            {
                if (!string.Equals(instrument.Name, spec.InstrumentName, StringComparison.OrdinalIgnoreCase)) continue;
                switch (spec.ValueType)
                {
                    case CollectedValueType.Counter:
                        var ckey = MakeKey(pack.Id, instrument.Name, CollectedValueType.Counter);
                        var cs = _counters.GetOrAdd(ckey, _ => new InstrumentState());
                        Interlocked.Add(ref cs.Total, (long)value);
                        break;
                    case CollectedValueType.Gauge:
                        _gauges[MakeKey(pack.Id, instrument.Name, CollectedValueType.Gauge)] = value;
                        break;
                    case CollectedValueType.Histogram_P50:
                    case CollectedValueType.Histogram_P95:
                    case CollectedValueType.Histogram_P99:
                        var vtype = spec.ValueType == CollectedValueType.Histogram_P50 ? "p50"
                            : spec.ValueType == CollectedValueType.Histogram_P95 ? "p95" : "p99";
                        var hkey = MakeKey(pack.Id, $"{instrument.Name}|{vtype}", spec.ValueType);
                        lock (_histLock)
                            _histograms.GetOrAdd(hkey, _ => new List<double>()).Add(value);
                        break;
                }
            }
        }
    }

    private string GetMeterName(string packId, string instrument)
    {
        var bare = instrument.Split('|')[0];
        IMonitoringPack? pack;
        lock (_packsLock)
        {
            if (!_packs.TryGetValue(packId, out pack))
                return string.Empty;
        }
        foreach (var group in pack.MeterGroups)
        foreach (var spec in group.Instruments)
            if (spec.InstrumentName == bare)
                return group.MeterName;
        return string.Empty;
    }

    private List<IMonitoringPack> SnapshotPacks()
    {
        lock (_packsLock)
            return _packs.Values.ToList();
    }

    private static string MakeKey(string packId, string instrument, CollectedValueType vtype)
        => $"{packId}::{instrument}::{(int)vtype}";

    private static (string packId, string instrument) ParseKey(string key)
    {
        var parts = key.Split("::");
        return (parts[0], parts[1]);
    }

    public override void Dispose()
    {
        if (_runtimeController is not null)
            _runtimeController.PackChanged -= OnPackChanged;
        _listener?.Dispose();
        base.Dispose();
    }

    private sealed class InstrumentState { public long Total; }
}

public interface IMetricSnapshotStoreAccessor
{
    IMetricSnapshotStore GetStore();
}
