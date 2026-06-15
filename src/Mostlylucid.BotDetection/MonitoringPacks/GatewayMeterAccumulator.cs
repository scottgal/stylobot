using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.MonitoringPacks;

/// <summary>
///     Gateway-side meter accumulator. Subscribes to .NET
///     <see cref="MeterListener"/> measurements from every registered
///     <see cref="IMonitoringPack"/>, and exposes the latest accumulated
///     snapshot via <see cref="GetCurrentSnapshot"/> for the remote-mode
///     dashboard host to pull on demand. No periodic flush -- the remote
///     client owns its own polling cadence.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> whose
///         <c>ExecuteAsync</c> only called <see cref="StartListening"/> and
///         then returned; the rest of the loop body was empty. There is no
///         periodic work to schedule, so the migration collapses the class to
///         a plain singleton + <see cref="IDisposable"/> -- the BotDetection
///         bootstrap shim drives the eager resolution that runs
///         <see cref="StartListening"/> at boot. Per the Wave 2 plan, this is
///         the "registration collapses to a plain singleton on the
///         coordinator's resolver shim" case.
///     </para>
/// </summary>
public sealed class GatewayMeterAccumulator : IDisposable
{
    private readonly IReadOnlyList<IMonitoringPack> _packs;
    private readonly ILogger<GatewayMeterAccumulator> _logger;
    private MeterListener? _listener;
    private int _disposed;

    private readonly ConcurrentDictionary<string, long> _counters = new();
    private readonly ConcurrentDictionary<string, double> _gauges = new();
    private readonly ConcurrentDictionary<string, List<double>> _histograms = new();
    private readonly object _histLock = new();

    public GatewayMeterAccumulator(
        IEnumerable<IMonitoringPack> packs,
        ILogger<GatewayMeterAccumulator> logger)
    {
        _packs = packs.ToList();
        _logger = logger;
        StartListening();
        _logger.LogInformation("GatewayMeterAccumulator started");
    }

    public void StartListening()
    {
        _listener?.Dispose();
        _listener = new MeterListener();
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            foreach (var pack in _packs)
            foreach (var group in pack.MeterGroups)
            {
                if (!string.Equals(instrument.Meter.Name, group.MeterName, StringComparison.OrdinalIgnoreCase)) continue;
                if (group.Instruments.Any(s => string.Equals(s.InstrumentName, instrument.Name, StringComparison.OrdinalIgnoreCase)))
                    listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((inst, val, _, _) => Accumulate(inst, (double)val));
        _listener.SetMeasurementEventCallback<double>((inst, val, _, _) => Accumulate(inst, val));
        _listener.SetMeasurementEventCallback<int>((inst, val, _, _) => Accumulate(inst, (double)val));
        _listener.Start();
    }

    public MetricSnapshotDto[] GetCurrentSnapshot()
    {
        _listener?.RecordObservableInstruments();
        var bucket = DateTime.UtcNow.TruncateToMinute();
        var results = new List<MetricSnapshotDto>();

        foreach (var pack in _packs)
        foreach (var group in pack.MeterGroups)
        foreach (var spec in group.Instruments)
        {
            var key = $"{pack.Id}::{group.MeterName}::{spec.InstrumentName}::{spec.ValueType}";
            switch (spec.ValueType)
            {
                case CollectedValueType.Counter:
                    if (_counters.TryGetValue(key, out var cnt))
                        results.Add(new MetricSnapshotDto(bucket, pack.Id, group.MeterName, spec.InstrumentName, null, (double)cnt, "rate"));
                    break;
                case CollectedValueType.Gauge:
                    if (_gauges.TryGetValue(key, out var g))
                        results.Add(new MetricSnapshotDto(bucket, pack.Id, group.MeterName, spec.InstrumentName, null, g, "gauge"));
                    break;
                case CollectedValueType.Histogram_P50:
                case CollectedValueType.Histogram_P95:
                case CollectedValueType.Histogram_P99:
                    lock (_histLock)
                    {
                        if (!_histograms.TryGetValue(key, out var obs) || obs.Count == 0) break;
                        var sorted = obs.OrderBy(v => v).ToList();
                        var pct = spec.ValueType == CollectedValueType.Histogram_P50 ? 0.50
                            : spec.ValueType == CollectedValueType.Histogram_P95 ? 0.95 : 0.99;
                        var idx = Math.Clamp((int)Math.Ceiling(pct * sorted.Count) - 1, 0, sorted.Count - 1);
                        var vtype = spec.ValueType == CollectedValueType.Histogram_P50 ? "p50"
                            : spec.ValueType == CollectedValueType.Histogram_P95 ? "p95" : "p99";
                        results.Add(new MetricSnapshotDto(bucket, pack.Id, group.MeterName, spec.InstrumentName, null, sorted[idx], vtype));
                    }
                    break;
            }
        }

        return results.ToArray();
    }

    private void Accumulate(Instrument instrument, double value)
    {
        foreach (var pack in _packs)
        foreach (var group in pack.MeterGroups)
        {
            if (!string.Equals(instrument.Meter.Name, group.MeterName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (var spec in group.Instruments)
            {
                if (!string.Equals(instrument.Name, spec.InstrumentName, StringComparison.OrdinalIgnoreCase)) continue;
                var key = $"{pack.Id}::{group.MeterName}::{spec.InstrumentName}::{spec.ValueType}";
                switch (spec.ValueType)
                {
                    case CollectedValueType.Counter:
                        _counters.AddOrUpdate(key, (long)value, (_, old) => old + (long)value);
                        break;
                    case CollectedValueType.Gauge:
                        _gauges[key] = value;
                        break;
                    default:
                        lock (_histLock)
                            _histograms.GetOrAdd(key, _ => new List<double>()).Add(value);
                        break;
                }
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _listener?.Dispose();
    }
}

public sealed record MetricSnapshotDto(
    DateTime BucketTime,
    string PackId,
    string MeterName,
    string Instrument,
    string? Tags,
    double Value,
    string ValueType);