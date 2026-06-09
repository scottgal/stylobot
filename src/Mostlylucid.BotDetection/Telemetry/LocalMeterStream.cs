using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Mostlylucid.BotDetection.Telemetry;

/// <summary>
///     In-process implementation of <see cref="IMeterStream" /> that subscribes to every
///     <see cref="Meter" /> the gateway publishes via <see cref="MeterListener" /> and keeps
///     a bounded ring buffer of samples per instrument name. Self-bootstraps as
///     <see cref="IHostedService" /> so registration is one <c>AddHostedService</c> call.
/// </summary>
/// <remarks>
///     <para>
///         <b>Sample storage:</b> a <see cref="ConcurrentDictionary{TKey,TValue}" /> keyed by
///         instrument name, each value a small lock-guarded ring buffer of
///         <c>(timestamp, value)</c> pairs.
///     </para>
///     <para>
///         <b>Aggregation:</b> counters are stored cumulatively and emitted as deltas-per-bucket;
///         gauges / up-down expose average-per-bucket; histograms expose average plus P50/P99
///         across the window.
///     </para>
///     <para>
///         <b>Tests:</b> set <see cref="LocalMeterStreamOptions.MeterNamePrefixFilter" /> to a
///         per-test unique prefix and use <see cref="PumpObservables" /> to force observable
///         callbacks rather than relying on a wall-clock tick.
///     </para>
/// </remarks>
public sealed class LocalMeterStream : IMeterStream, IHostedService, IAsyncDisposable, IDisposable
{
    private readonly LocalMeterStreamOptions _options;
    private readonly ILogger<LocalMeterStream> _logger;

    private readonly ConcurrentDictionary<string, MeterCatalogEntry> _catalog = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, RingBuffer> _buffers = new(StringComparer.Ordinal);

    private MeterListener? _listener;
    private int _disposed;

    public LocalMeterStream(IOptions<LocalMeterStreamOptions> options, ILogger<LocalMeterStream> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    ///     Starts the underlying <see cref="MeterListener" /> and registers measurement callbacks
    ///     for the numeric primitives. Idempotent: a second call is a no-op.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_listener != null)
            return Task.CompletedTask;

        var listener = new MeterListener
        {
            InstrumentPublished = OnInstrumentPublished,
        };

        listener.SetMeasurementEventCallback<int>(OnMeasurementInt);
        listener.SetMeasurementEventCallback<long>(OnMeasurementLong);
        listener.SetMeasurementEventCallback<double>(OnMeasurementDouble);
        listener.SetMeasurementEventCallback<float>(OnMeasurementFloat);
        listener.SetMeasurementEventCallback<decimal>(OnMeasurementDecimal);
        listener.SetMeasurementEventCallback<short>(OnMeasurementShort);
        listener.SetMeasurementEventCallback<byte>(OnMeasurementByte);

        listener.Start();
        _listener = listener;

        _logger.LogDebug(
            "LocalMeterStream started (RingBufferCapacity={Capacity}, PrefixFilter={Prefix})",
            _options.RingBufferCapacity,
            string.IsNullOrEmpty(_options.MeterNamePrefixFilter) ? "<all>" : _options.MeterNamePrefixFilter);

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Stops the listener and clears the catalog + sample buffers so a subsequent
    ///     <see cref="StartAsync" /> begins fresh.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeCore();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Forces the listener to invoke every observable instrument's callback. Used by tests
    ///     to drive observable gauges deterministically without sleeping on a wall-clock tick.
    /// </summary>
    public void PumpObservables()
    {
        _listener?.RecordObservableInstruments();
    }

    public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
    {
        var snapshot = _catalog.Values.ToArray();
        IReadOnlyList<MeterCatalogEntry> result = snapshot;
        return Task.FromResult(result);
    }

    public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
    {
        if (buckets <= 0)
            throw new ArgumentOutOfRangeException(nameof(buckets), buckets, "Bucket count must be positive.");
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), window, "Window must be positive.");

        if (!_catalog.TryGetValue(meterName, out var entry))
            return Task.FromResult<MeterTimeSeries?>(null);

        if (!_buffers.TryGetValue(meterName, out var buffer))
        {
            // Instrument is in the catalog but no samples have landed yet: return an all-zero series.
            return Task.FromResult<MeterTimeSeries?>(BuildEmpty(entry, window, buckets));
        }

        var samples = buffer.Snapshot();
        var now = DateTimeOffset.UtcNow;
        var bucketSize = TimeSpan.FromTicks(window.Ticks / buckets);
        var windowStart = now - window;

        var bucketStarts = new DateTimeOffset[buckets];
        for (var i = 0; i < buckets; i++)
            bucketStarts[i] = windowStart + TimeSpan.FromTicks(bucketSize.Ticks * i);

        var values = new double[buckets];

        // Sort by timestamp ascending so cumulative-delta logic is well-defined.
        Array.Sort(samples, static (a, b) => a.Timestamp.CompareTo(b.Timestamp));

        var current = samples.Length > 0 ? samples[^1].Value : 0d;

        if (entry.Kind == MeterKind.Counter)
        {
            // For counters: the bucket value is (last cumulative in bucket) - (last cumulative seen prior).
            // "Prior" for the first bucket is the cumulative reading at the start of the window, which we
            // approximate as the most recent sample BEFORE the window. If there is none, prior = 0.
            var prior = 0d;
            var priorSet = false;
            foreach (var sample in samples)
            {
                if (sample.Timestamp < windowStart)
                {
                    prior = sample.Value;
                    priorSet = true;
                }
                else
                {
                    break;
                }
            }
            if (!priorSet)
                prior = 0d;

            var bucketLast = new double?[buckets];
            foreach (var sample in samples)
            {
                if (sample.Timestamp < windowStart) continue;
                if (sample.Timestamp > now) continue;

                var idx = (int)((sample.Timestamp - windowStart).Ticks / bucketSize.Ticks);
                if (idx < 0) idx = 0;
                if (idx >= buckets) idx = buckets - 1;

                bucketLast[idx] = sample.Value;
            }

            var running = prior;
            for (var i = 0; i < buckets; i++)
            {
                if (bucketLast[i].HasValue)
                {
                    var last = bucketLast[i]!.Value;
                    values[i] = last - running;
                    running = last;
                }
                else
                {
                    values[i] = 0d;
                }
            }
        }
        else
        {
            // Gauge / UpDownCounter / Histogram: average samples landing in each bucket.
            var sums = new double[buckets];
            var counts = new int[buckets];
            foreach (var sample in samples)
            {
                if (sample.Timestamp < windowStart) continue;
                if (sample.Timestamp > now) continue;

                var idx = (int)((sample.Timestamp - windowStart).Ticks / bucketSize.Ticks);
                if (idx < 0) idx = 0;
                if (idx >= buckets) idx = buckets - 1;

                sums[idx] += sample.Value;
                counts[idx]++;
            }

            for (var i = 0; i < buckets; i++)
                values[i] = counts[i] > 0 ? sums[i] / counts[i] : 0d;
        }

        double? p50 = null;
        double? p99 = null;
        if (entry.Kind == MeterKind.Histogram)
        {
            // Percentiles across the windowed sample set, not per-bucket.
            var windowSamples = new List<double>(samples.Length);
            foreach (var sample in samples)
            {
                if (sample.Timestamp < windowStart) continue;
                if (sample.Timestamp > now) continue;
                windowSamples.Add(sample.Value);
            }
            if (windowSamples.Count > 0)
            {
                windowSamples.Sort();
                p50 = Percentile(windowSamples, 0.50);
                p99 = Percentile(windowSamples, 0.99);
            }
            else
            {
                p50 = 0d;
                p99 = 0d;
            }
        }

        var ts = new MeterTimeSeries(
            entry.Name,
            entry.Kind,
            bucketStarts,
            values,
            current,
            p50,
            p99);

        return Task.FromResult<MeterTimeSeries?>(ts);
    }

    // -------------------- listener callbacks --------------------

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        var filter = _options.MeterNamePrefixFilter;
        if (!string.IsNullOrEmpty(filter)
            && !instrument.Name.StartsWith(filter, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var kind = InferKind(instrument);
        var ns = ExtractNamespace(instrument.Name);
        var entry = new MeterCatalogEntry(
            instrument.Name,
            ns,
            kind,
            string.IsNullOrEmpty(instrument.Unit) ? null : instrument.Unit,
            string.IsNullOrEmpty(instrument.Description) ? null : instrument.Description);

        _catalog[instrument.Name] = entry;
        listener.EnableMeasurementEvents(instrument);
    }

    private void OnMeasurementInt(Instrument instrument, int measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Push(instrument, measurement);

    private void OnMeasurementLong(Instrument instrument, long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Push(instrument, measurement);

    private void OnMeasurementDouble(Instrument instrument, double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Push(instrument, measurement);

    private void OnMeasurementFloat(Instrument instrument, float measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Push(instrument, measurement);

    private void OnMeasurementDecimal(Instrument instrument, decimal measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Push(instrument, (double)measurement);

    private void OnMeasurementShort(Instrument instrument, short measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Push(instrument, measurement);

    private void OnMeasurementByte(Instrument instrument, byte measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Push(instrument, measurement);

    private void Push(Instrument instrument, double value)
    {
        if (!_catalog.TryGetValue(instrument.Name, out var entry))
            return; // Listener fired before catalog populated; safe to drop, will catch the next sample.

        var buffer = _buffers.GetOrAdd(instrument.Name, _ => new RingBuffer(_options.RingBufferCapacity));

        double stored;
        if (entry.Kind == MeterKind.Counter)
        {
            // Counters in System.Diagnostics.Metrics report INCREMENTS via Add(). Convert to cumulative
            // so GetAsync can compute bucket deltas relative to the start of the window.
            stored = buffer.LastValueOrZero() + value;
        }
        else
        {
            stored = value;
        }

        buffer.Push(new MeterSample(DateTimeOffset.UtcNow, stored));
    }

    // -------------------- helpers --------------------

    private static MeterKind InferKind(Instrument instrument)
    {
        var typeName = instrument.GetType().Name;

        // Generic type names come back as "Counter`1", "Histogram`1", etc. Strip the arity suffix.
        var tick = typeName.IndexOf('`');
        if (tick > 0) typeName = typeName[..tick];

        return typeName switch
        {
            "Counter" => MeterKind.Counter,
            "UpDownCounter" => MeterKind.UpDownCounter,
            "Histogram" => MeterKind.Histogram,
            "ObservableCounter" => MeterKind.Counter,
            "ObservableGauge" => MeterKind.Gauge,
            "ObservableUpDownCounter" => MeterKind.UpDownCounter,
            _ => MeterKind.Gauge,
        };
    }

    private static string ExtractNamespace(string name)
    {
        if (string.IsNullOrEmpty(name)) return string.Empty;
        var underscore = name.IndexOf('_');
        var dot = name.IndexOf('.');
        var idx = -1;
        if (underscore >= 0 && dot >= 0) idx = Math.Min(underscore, dot);
        else if (underscore >= 0) idx = underscore;
        else if (dot >= 0) idx = dot;
        return idx > 0 ? name[..idx] : name;
    }

    private static MeterTimeSeries BuildEmpty(MeterCatalogEntry entry, TimeSpan window, int buckets)
    {
        var now = DateTimeOffset.UtcNow;
        var bucketSize = TimeSpan.FromTicks(window.Ticks / buckets);
        var windowStart = now - window;
        var bucketStarts = new DateTimeOffset[buckets];
        for (var i = 0; i < buckets; i++)
            bucketStarts[i] = windowStart + TimeSpan.FromTicks(bucketSize.Ticks * i);
        var values = new double[buckets];
        double? p50 = entry.Kind == MeterKind.Histogram ? 0d : null;
        double? p99 = entry.Kind == MeterKind.Histogram ? 0d : null;
        return new MeterTimeSeries(entry.Name, entry.Kind, bucketStarts, values, 0d, p50, p99);
    }

    private static double Percentile(List<double> sortedSamples, double percentile)
    {
        if (sortedSamples.Count == 0) return 0d;
        if (sortedSamples.Count == 1) return sortedSamples[0];

        var rank = percentile * (sortedSamples.Count - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedSamples[lower];

        var weight = rank - lower;
        return sortedSamples[lower] * (1 - weight) + sortedSamples[upper] * weight;
    }

    // -------------------- lifecycle --------------------

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            _listener?.Dispose();
        }
        catch
        {
            // Listener disposal swallows transient errors; nothing to surface here.
        }

        _listener = null;
        _catalog.Clear();
        _buffers.Clear();
    }

    public void Dispose() => DisposeCore();

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }

    // -------------------- nested helpers --------------------

    /// <summary>A single sample observed by the listener.</summary>
    private readonly record struct MeterSample(DateTimeOffset Timestamp, double Value);

    /// <summary>
    ///     Tiny fixed-capacity circular buffer guarded by a lock. We don't need a high-throughput
    ///     lock-free ring here -- measurement callbacks aren't on the hottest path and per-meter
    ///     contention is naturally sharded.
    /// </summary>
    private sealed class RingBuffer
    {
        private readonly object _gate = new();
        private readonly MeterSample[] _slots;
        private int _head;
        private int _count;
        private double _lastValue;

        public RingBuffer(int capacity)
        {
            if (capacity <= 0) capacity = 1;
            _slots = new MeterSample[capacity];
        }

        public void Push(MeterSample sample)
        {
            lock (_gate)
            {
                _slots[_head] = sample;
                _head = (_head + 1) % _slots.Length;
                if (_count < _slots.Length) _count++;
                _lastValue = sample.Value;
            }
        }

        public double LastValueOrZero()
        {
            lock (_gate)
            {
                return _count == 0 ? 0d : _lastValue;
            }
        }

        public MeterSample[] Snapshot()
        {
            lock (_gate)
            {
                var result = new MeterSample[_count];
                if (_count == 0) return result;

                if (_count < _slots.Length)
                {
                    // Buffer not yet wrapped: oldest at index 0, newest at _head - 1.
                    Array.Copy(_slots, 0, result, 0, _count);
                }
                else
                {
                    // Wrapped: oldest at _head, walk forward.
                    var tail = _slots.Length - _head;
                    Array.Copy(_slots, _head, result, 0, tail);
                    Array.Copy(_slots, 0, result, tail, _head);
                }
                return result;
            }
        }
    }
}