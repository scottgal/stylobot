using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.PrometheusPack.Telemetry;

/// <summary>
///     In-process implementation of <see cref="IMeterStream" /> that subscribes
///     to every <see cref="Meter" /> the gateway publishes via
///     <see cref="MeterListener" /> and keeps an LFU sliding cache of
///     <see cref="MeterSummaryAtom" />s -- one per instrument name -- summarising
///     the recent traffic.
/// </summary>
/// <remarks>
///     <para>
///         <b>Storage shape:</b> a <see cref="ConcurrentDictionary{TKey,TValue}" />
///         keyed by instrument name, each value a <see cref="MeterSummaryAtom" />
///         that holds the most recent observed value, a small ring of recent
///         per-bucket aggregates, and -- for histograms only -- a bounded
///         percentile reservoir. <b>No raw per-sample ring buffers are kept</b>:
///         summaries are truth, the underlying samples are discarded after
///         aggregation. This matches the project's write-behind / LFU façade
///         pattern documented in <c>SqlitePathLifecycleStore</c> and
///         <c>PolicyEffectivenessCache</c>.
///     </para>
///     <para>
///         <b>Eviction:</b> a periodic sweep (default every 60s) decays every
///         atom's <c>HitCount</c> by <see cref="LocalMeterStreamOptions.HitCountDecayFactor" />
///         (sliding LFU) and, when the dictionary exceeds
///         <see cref="LocalMeterStreamOptions.MaxTrackedMeters" />, evicts atoms
///         in ascending-HitCount order until the count is back under cap.
///     </para>
///     <para>
///         <b>Signal emission:</b> every <c>Record()</c> call may emit a
///         <see cref="MeterSignal" /> to the registered <see cref="IMeterSignalSink" />
///         subject to <see cref="LocalMeterStreamOptions.MinSignalEmitInterval" />
///         coalescing. Phase F wires the blackboard adapter; Phase G adds the
///         oscilloscope-style trigger service.
///     </para>
///     <para>
///         <b>Aggregation by kind:</b>
///         <list type="bullet">
///             <item><b>Counter:</b> per-bucket value is the sum of cumulative
///             deltas observed inside the bucket; <c>Current</c> is the running
///             cumulative.</item>
///             <item><b>UpDownCounter:</b> identical to Counter but accepts
///             signed deltas.</item>
///             <item><b>Gauge:</b> per-bucket value is the arithmetic mean of
///             observations; <c>Current</c> is the latest observation.</item>
///             <item><b>Histogram:</b> per-bucket value is the arithmetic mean
///             of recorded values; <c>Current</c> is the latest recorded value;
///             P50 / P99 come from the bounded reservoir of recent values.</item>
///         </list>
///     </para>
///     <para>
///         <b>Tests:</b> set <see cref="LocalMeterStreamOptions.MeterNamePrefixFilter" />
///         to a per-test unique prefix and use <see cref="PumpObservables" /> to
///         force observable callbacks rather than relying on a wall-clock tick.
///     </para>
/// </remarks>
public sealed class LocalMeterStream : IMeterStream, IAsyncDisposable, IDisposable
{
    private readonly LocalMeterStreamOptions _options;
    private readonly ILogger<LocalMeterStream> _logger;
    private readonly IMeterSignalSink _signalSink;
    private readonly MeterDescriptionRegistry? _descriptions;

    private readonly ConcurrentDictionary<string, MeterSummaryAtom> _atoms = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, MeterCatalogEntry> _catalog = new(StringComparer.Ordinal);
    private readonly object _evictionGate = new();

    private readonly IDisposable _decaySubscription;
    private MeterListener? _listener;
    private int _disposed;

    public LocalMeterStream(
        IOptions<LocalMeterStreamOptions> options,
        ILogger<LocalMeterStream> logger)
        : this(options, logger, new NullMeterSignalSink(), NullScheduleCoordinator.Instance)
    {
    }

    public LocalMeterStream(
        IOptions<LocalMeterStreamOptions> options,
        ILogger<LocalMeterStream> logger,
        IMeterSignalSink signalSink)
        : this(options, logger, signalSink, NullScheduleCoordinator.Instance)
    {
    }

    /// <summary>
    ///     Production constructor. Starts the underlying
    ///     <see cref="MeterListener" /> immediately so callers don't need to
    ///     drive a separate <c>StartAsync</c> -- the listener is event-driven,
    ///     not periodic, so the constructor is the right moment to subscribe.
    ///     The periodic LFU-decay / eviction sweep is wired through
    ///     <paramref name="coordinator"/>'s <see cref="TickCadence.Tick1m"/>
    ///     subscription (Wave 2 of the architectural-drift remediation; see
    ///     <c>feedback_no_background_services</c>).
    /// </summary>
    public LocalMeterStream(
        IOptions<LocalMeterStreamOptions> options,
        ILogger<LocalMeterStream> logger,
        IMeterSignalSink signalSink,
        IScheduleCoordinator coordinator,
        MeterDescriptionRegistry? descriptions = null)
    {
        ArgumentNullException.ThrowIfNull(coordinator);
        _options = options.Value;
        _logger = logger;
        _signalSink = signalSink;
        _descriptions = descriptions;

        StartListener();
        _decaySubscription = coordinator.Subscribe(
            TickCadence.Tick1m,
            "LocalMeterStream.Decay",
            CostHint.Low,
            (_, _) => { DecayAndEvict(); return Task.CompletedTask; });

        _logger.LogDebug(
            "LocalMeterStream started (MaxTrackedMeters={Max}, RecentBucketCount={Buckets}, BucketWidth={Width}, PrefixFilter={Prefix})",
            _options.MaxTrackedMeters,
            _options.RecentBucketCount,
            _options.BucketWidth,
            string.IsNullOrEmpty(_options.MeterNamePrefixFilter) ? "<all>" : _options.MeterNamePrefixFilter);
    }

    private void StartListener()
    {
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
    }

    /// <summary>
    ///     Backwards-compatible no-op kept for test rigs that called
    ///     <c>StartAsync</c> on the pre-Wave-2 <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
    ///     shape. The listener now starts in the constructor; this method exists
    ///     so the existing call sites stay green without rewriting.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>
    ///     Backwards-compatible shim that calls <see cref="DisposeCore"/>.
    /// </summary>
    public Task StopAsync(CancellationToken cancellationToken)
    {
        DisposeCore();
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Forces the listener to invoke every observable instrument's callback.
    ///     Used by tests to drive observable gauges deterministically without
    ///     sleeping on a wall-clock tick.
    /// </summary>
    public void PumpObservables()
    {
        _listener?.RecordObservableInstruments();
    }

    /// <summary>
    ///     Test hook: forces a single decay + eviction sweep on the calling
    ///     thread. Used by tests to assert LFU behaviour without waiting for
    ///     the timer.
    /// </summary>
    internal void PumpEvictionForTesting()
    {
        DecayAndEvict();
    }

    /// <inheritdoc />
    /// <remarks>
    ///     Returns entries in <b>most-recently-touched-first</b> order so
    ///     callers paginating get the freshest meters at the top.
    /// </remarks>
    public Task<IReadOnlyList<MeterCatalogEntry>> ListAsync(CancellationToken ct)
    {
        // Snapshot once; ordering by LastTouched is best-effort over a moving
        // dictionary -- consistency-of-output is what matters, not strict
        // global ordering.
        var snapshot = _atoms.Values.ToArray();
        Array.Sort(snapshot, static (a, b) => b.LastTouched.CompareTo(a.LastTouched));

        var result = new MeterCatalogEntry[snapshot.Length];
        for (var i = 0; i < snapshot.Length; i++)
            result[i] = snapshot[i].Catalog;

        return Task.FromResult<IReadOnlyList<MeterCatalogEntry>>(result);
    }

    /// <inheritdoc />
    public Task<MeterTimeSeries?> GetAsync(string meterName, TimeSpan window, int buckets, CancellationToken ct)
    {
        if (buckets <= 0)
            throw new ArgumentOutOfRangeException(nameof(buckets), buckets, "Bucket count must be positive.");
        if (window <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(window), window, "Window must be positive.");

        if (!_atoms.TryGetValue(meterName, out var atom))
            return Task.FromResult<MeterTimeSeries?>(null);

        var ts = atom.BuildTimeSeries(window, buckets, DateTimeOffset.UtcNow);
        return Task.FromResult<MeterTimeSeries?>(ts);
    }

    // -------------------- listener callbacks --------------------

    private void OnInstrumentPublished(Instrument instrument, MeterListener listener)
    {
        if (!MeterNamePrefixFilterMatcher.Matches(instrument.Name, _options.MeterNamePrefixFilter))
        {
            return;
        }

        var kind = InferKind(instrument);
        var ns = ExtractNamespace(instrument.Name);
        var desc = _descriptions?.TryGet(instrument.Name);
        var entry = new MeterCatalogEntry(
            instrument.Name,
            ns,
            kind,
            desc?.Unit ?? (string.IsNullOrEmpty(instrument.Unit) ? null : instrument.Unit),
            desc?.Description ?? (string.IsNullOrEmpty(instrument.Description) ? null : instrument.Description),
            desc?.Label);

        // Remember the catalog entry for this name so subsequent Record()
        // calls can lazily re-create the atom if an LFU eviction destroyed
        // the previous one. The catalog map is small (one entry per known
        // instrument) and is NOT subject to LFU eviction itself.
        _catalog[instrument.Name] = entry;
        listener.EnableMeasurementEvents(instrument);
    }

    private void OnMeasurementInt(Instrument instrument, int measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, measurement);

    private void OnMeasurementLong(Instrument instrument, long measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, measurement);

    private void OnMeasurementDouble(Instrument instrument, double measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, measurement);

    private void OnMeasurementFloat(Instrument instrument, float measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, measurement);

    private void OnMeasurementDecimal(Instrument instrument, decimal measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, (double)measurement);

    private void OnMeasurementShort(Instrument instrument, short measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, measurement);

    private void OnMeasurementByte(Instrument instrument, byte measurement,
        ReadOnlySpan<KeyValuePair<string, object?>> tags, object? state) => Record(instrument, measurement);

    private void Record(Instrument instrument, double value)
    {
        // Lazy-create the atom on first measurement (or after an LFU eviction
        // destroyed the previous one). Catalog entry must already exist --
        // OnInstrumentPublished populates it whenever this stream's filter
        // accepts the instrument.
        if (!_catalog.TryGetValue(instrument.Name, out var entry))
            return;

        var atom = _atoms.GetOrAdd(instrument.Name, _ => new MeterSummaryAtom(entry, _options));

        var now = DateTimeOffset.UtcNow;
        var signal = atom.Record(value, now);
        if (signal is not null)
        {
            try
            {
                _signalSink.OnMeter(signal);
            }
            catch (Exception ex)
            {
                // Sink contract says implementations must not throw, but be
                // defensive: a buggy sink on the listener thread can sink the
                // whole listener.
                _logger.LogDebug(ex, "MeterSignalSink threw for {Name}", atom.Catalog.Name);
            }
        }
    }

    // -------------------- LFU sweep --------------------

    private void DecayAndEvict()
    {
        if (_disposed != 0) return;

        // Decay every atom's HitCount. No lock -- we accept a racing Record()
        // losing a single increment in the rare overlap.
        var factor = _options.HitCountDecayFactor;
        if (factor < 0d) factor = 0d;
        if (factor > 1d) factor = 1d;

        foreach (var atom in _atoms.Values)
        {
            atom.DecayHitCount(factor);
        }

        // Evict in ascending-HitCount order until under cap.
        var max = _options.MaxTrackedMeters;
        if (max <= 0) max = 1;
        if (_atoms.Count <= max) return;

        lock (_evictionGate)
        {
            while (_atoms.Count > max)
            {
                string? coldestKey = null;
                double coldestHit = double.MaxValue;

                foreach (var pair in _atoms)
                {
                    var hit = pair.Value.SnapshotHitCount();
                    if (hit < coldestHit)
                    {
                        coldestHit = hit;
                        coldestKey = pair.Key;
                    }
                }

                if (coldestKey is null) break;
                _atoms.TryRemove(coldestKey, out _);
            }
        }
    }

    // -------------------- helpers --------------------

    private static MeterKind InferKind(Instrument instrument)
    {
        var typeName = instrument.GetType().Name;

        // Generic type names come back as "Counter`1", "Histogram`1", etc.
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

    // -------------------- lifecycle --------------------

    private void DisposeCore()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try { _decaySubscription.Dispose(); }
        catch { /* swallow -- coordinator already torn down */ }

        try { _listener?.Dispose(); }
        catch { /* swallow */ }

        _listener = null;
        _atoms.Clear();
        _catalog.Clear();
    }

    public void Dispose() => DisposeCore();

    public ValueTask DisposeAsync()
    {
        DisposeCore();
        return ValueTask.CompletedTask;
    }
}
