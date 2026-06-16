using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.MonitoringPacks;

/// <summary>
///     Subscribes to .NET <see cref="MeterListener"/> measurements from every
///     registered <see cref="IMonitoringPack"/>, accumulates them in
///     write-through bounded dictionaries, and on a periodic flush writes the
///     aggregated <see cref="MetricSnapshot"/> rows to the snapshot store. The
///     in-memory accumulators are pre-flush write-behind buffers; persistence
///     lives in the store, so a restart loses at most the current bucket.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> called <see cref="StartListening"/> once and
///         then looped on <c>Task.Delay(packs.Min(p =&gt; p.CollectionInterval))</c>
///         + flush. Now subscribes to <see cref="TickCadence.Tick1m"/> -- the
///         default pack <see cref="AspNetMonitoringPack.CollectionInterval"/>
///         is 60 s, which aligns to Tick1m exactly. Packs configured with a
///         sub-minute collection interval flush on the same Tick1m boundary;
///         the loss-resolution is the snapshot bucket
///         (<see cref="DateTimeExtensions.TruncateToMinute"/>) which is also
///         minute-aligned, so the alignment is correct.
///     </para>
/// </summary>
public sealed class MeterListenerService : IDisposable
{
    private readonly Dictionary<string, IMonitoringPack> _packs;
    private readonly IMetricSnapshotStoreAccessor? _storeAccessor;
    private readonly ILogger<MeterListenerService> _logger;
    private readonly IPackRuntimeController? _runtimeController;
    private readonly IDisposable? _subscription;
    private readonly object _packsLock = new();
    private MeterListener? _listener;
    private int _disposed;

    private readonly ConcurrentDictionary<string, InstrumentState> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, List<double>> _histograms = new();
    private readonly object _histLock = new();

    private readonly IMetricSnapshotStore? _directStore;

    public MeterListenerService(
        IEnumerable<IMonitoringPack> packs,
        IMetricSnapshotStore store,
        ILogger<MeterListenerService> logger,
        IPackRuntimeController? runtimeController = null,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _packs = packs.ToDictionary(p => p.Id);
        _directStore = store;
        _logger = logger;
        _runtimeController = runtimeController;
        SubscribeRuntimeController();
        _subscription = SubscribeToCoordinator(scheduleCoordinator);
    }

    public MeterListenerService(
        IEnumerable<IMonitoringPack> packs,
        IMetricSnapshotStoreAccessor storeAccessor,
        ILogger<MeterListenerService> logger,
        IPackRuntimeController? runtimeController = null,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _packs = packs.ToDictionary(p => p.Id);
        _storeAccessor = storeAccessor;
        _logger = logger;
        _runtimeController = runtimeController;
        SubscribeRuntimeController();
        _subscription = SubscribeToCoordinator(scheduleCoordinator);
    }

    private IDisposable? SubscribeToCoordinator(IScheduleCoordinator? scheduleCoordinator)
    {
        if (scheduleCoordinator is null) return null;

        // Start the meter listener at construction so measurements begin
        // accumulating before the first flush tick. The legacy ExecuteAsync
        // called StartListening as its first action; the subscribe-in-ctor
        // model puts it next to the subscription so both happen on the same
        // boot path.
        StartListening();
        _logger.LogInformation("MeterListenerService started with {Count} pack(s)", _packs.Count);

        return scheduleCoordinator.Subscribe(
            TickCadence.Tick1m,
            "MeterListenerService",
            CostHint.Low,
            OnTickAsync);
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

    /// <summary>
    ///     ScheduleCoordinator tick handler. Drains the in-memory accumulators
    ///     into snapshot rows and writes them to the store. Public so tests
    ///     can drive a single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        try
        {
            var store = _directStore ?? _storeAccessor!.GetStore();
            var snapshots = await FlushSnapshotsAsync(ct);
            if (snapshots.Count > 0)
                await store.WriteSnapshotsAsync(snapshots, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _logger.LogWarning(ex, "Failed to flush metric snapshots");
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

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_runtimeController is not null)
            _runtimeController.PackChanged -= OnPackChanged;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
        _listener?.Dispose();
    }

    private sealed class InstrumentState { public long Total; }
}

public interface IMetricSnapshotStoreAccessor
{
    IMetricSnapshotStore GetStore();
}