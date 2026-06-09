namespace Mostlylucid.BotDetection.PrometheusPack.Telemetry;

/// <summary>
///     Per-meter summary atom held by <see cref="LocalMeterStream" />. Holds
///     the most recent observed value, a ring of recent per-bucket aggregates,
///     a bounded percentile reservoir for histograms, and the LFU hit counter
///     used for sliding-decay eviction.
/// </summary>
/// <remarks>
///     <para>
///         <b>No raw sample storage.</b> Per the LFU-façade /
///         summary-not-stream rule, this atom keeps only what the dashboard
///         and the meter-signals pipeline need: <c>Current</c>, a small ring
///         of aggregates, and -- for histograms only -- a bounded reservoir.
///         The underlying measurements are discarded after the per-bucket
///         aggregate has incorporated them.
///     </para>
///     <para>
///         All state mutation is under a single per-atom monitor lock. Per-meter
///         contention is naturally sharded across the listener thread pool so
///         a single lock is not a bottleneck for any realistic load; for
///         counter-heavy workloads <see cref="LocalMeterStream" /> coalesces
///         signal emissions via
///         <see cref="LocalMeterStreamOptions.MinSignalEmitInterval" /> rather
///         than trying to reduce per-record lock pressure.
///     </para>
/// </remarks>
internal sealed class MeterSummaryAtom
{
    private readonly object _gate = new();
    private readonly LocalMeterStreamOptions _options;
    private readonly Bucket[] _buckets;
    private readonly double[]? _reservoir;

    private int _bucketHead;        // next write index into _buckets ring (circular)
    private int _bucketCount;       // number of populated _buckets slots (<= _buckets.Length)
    private int _reservoirHead;     // next write index into _reservoir
    private int _reservoirCount;    // number of populated _reservoir slots

    private double _hitCount;
    private double _cumulativePrev;
    private double _current;
    private DateTimeOffset _lastTouched;
    private DateTimeOffset _lastSignalEmittedAt;

    public MeterSummaryAtom(MeterCatalogEntry catalog, LocalMeterStreamOptions options)
    {
        Catalog = catalog;
        _options = options;

        var ringSize = options.RecentBucketCount;
        if (ringSize <= 0) ringSize = 1;
        _buckets = new Bucket[ringSize];

        if (catalog.Kind == MeterKind.Histogram)
        {
            var size = options.PercentileReservoirSize;
            if (size <= 0) size = 1;
            _reservoir = new double[size];
        }

        _lastTouched = DateTimeOffset.MinValue;
        _lastSignalEmittedAt = DateTimeOffset.MinValue;
    }

    /// <summary>The catalog entry the listener registered this atom under.</summary>
    public MeterCatalogEntry Catalog { get; }

    /// <summary>
    ///     Wall-clock of the most recent <see cref="Record" /> call. Used by
    ///     <see cref="LocalMeterStream.ListAsync" /> to surface
    ///     most-recently-touched meters first.
    /// </summary>
    public DateTimeOffset LastTouched
    {
        get
        {
            lock (_gate) return _lastTouched;
        }
    }

    /// <summary>
    ///     Records a single measurement. Returns a <see cref="MeterSignal" />
    ///     if the coalescing window has elapsed; returns null when the emission
    ///     is squelched (the underlying state is updated either way).
    /// </summary>
    public MeterSignal? Record(double value, DateTimeOffset now)
    {
        bool shouldEmit;
        MeterKind kind = Catalog.Kind;
        double currentSnapshot;
        double? p50 = null;
        double? p99 = null;

        lock (_gate)
        {
            _hitCount += 1d;
            _lastTouched = now;

            switch (kind)
            {
                case MeterKind.Counter:
                {
                    // System.Diagnostics.Metrics Counter<T>.Add() callbacks
                    // report the INCREMENT each call; we maintain cumulative
                    // here so GetAsync can compute deltas relative to bucket
                    // starts.
                    var newCumulative = _current + value;
                    var delta = value;
                    PushBucketAggregate(now, delta, signed: true, sample: false);
                    _cumulativePrev = newCumulative;
                    _current = newCumulative;
                    break;
                }
                case MeterKind.UpDownCounter:
                {
                    var newCumulative = _current + value;
                    var delta = value;
                    PushBucketAggregate(now, delta, signed: true, sample: false);
                    _cumulativePrev = newCumulative;
                    _current = newCumulative;
                    break;
                }
                case MeterKind.Gauge:
                {
                    PushBucketAggregate(now, value, signed: false, sample: true);
                    _current = value;
                    break;
                }
                case MeterKind.Histogram:
                {
                    PushBucketAggregate(now, value, signed: false, sample: true);
                    _current = value;
                    PushReservoir(value);
                    break;
                }
            }

            currentSnapshot = _current;

            var interval = _options.MinSignalEmitInterval;
            if (interval < TimeSpan.Zero) interval = TimeSpan.Zero;
            shouldEmit = (now - _lastSignalEmittedAt) >= interval;
            if (shouldEmit)
            {
                _lastSignalEmittedAt = now;
                if (kind == MeterKind.Histogram)
                {
                    (p50, p99) = ComputePercentilesLocked();
                }
            }
        }

        if (!shouldEmit) return null;
        return new MeterSignal(Catalog.Name, kind, currentSnapshot, p50, p99, now);
    }

    /// <summary>Applies the LFU sliding-decay factor to the hit count.</summary>
    public void DecayHitCount(double factor)
    {
        lock (_gate)
        {
            _hitCount *= factor;
        }
    }

    /// <summary>Snapshots the current HitCount for the eviction sweep.</summary>
    public double SnapshotHitCount()
    {
        lock (_gate)
        {
            return _hitCount;
        }
    }

    /// <summary>
    ///     Rebuilds a <see cref="MeterTimeSeries" /> covering the requested
    ///     window. Uses the bucket ring alone -- there is no raw-sample replay.
    /// </summary>
    public MeterTimeSeries BuildTimeSeries(TimeSpan window, int buckets, DateTimeOffset now)
    {
        var windowStart = now - window;
        var outBucketSize = TimeSpan.FromTicks(window.Ticks / buckets);

        var bucketStarts = new DateTimeOffset[buckets];
        for (var i = 0; i < buckets; i++)
            bucketStarts[i] = windowStart + TimeSpan.FromTicks(outBucketSize.Ticks * i);

        var values = new double[buckets];

        Bucket[] ringSnapshot;
        int ringCount;
        double currentSnapshot;
        MeterKind kind = Catalog.Kind;
        double? p50 = null;
        double? p99 = null;

        lock (_gate)
        {
            ringCount = _bucketCount;
            ringSnapshot = new Bucket[ringCount];
            if (ringCount > 0)
            {
                // Copy populated slots into a flat array. We only need the
                // values + their starts; order doesn't matter for aggregation.
                if (_bucketCount < _buckets.Length)
                {
                    Array.Copy(_buckets, 0, ringSnapshot, 0, ringCount);
                }
                else
                {
                    var tail = _buckets.Length - _bucketHead;
                    Array.Copy(_buckets, _bucketHead, ringSnapshot, 0, tail);
                    Array.Copy(_buckets, 0, ringSnapshot, tail, _bucketHead);
                }
            }
            currentSnapshot = _current;

            if (kind == MeterKind.Histogram)
                (p50, p99) = ComputePercentilesLocked();
        }

        if (ringCount == 0)
        {
            // No samples have landed: return an all-zero series with the
            // requested layout so callers don't have to special-case empty.
            return new MeterTimeSeries(
                Catalog.Name, kind, bucketStarts, values, currentSnapshot,
                kind == MeterKind.Histogram ? 0d : (double?)null,
                kind == MeterKind.Histogram ? 0d : (double?)null);
        }

        // Window shorter than a single recorded bucket: collapse to a single
        // value (the most recent bucket's aggregate) so dashboard widgets that
        // request very short windows don't see an artificial zero.
        if (window < _options.BucketWidth)
        {
            var latest = FindMostRecentBucket(ringSnapshot);
            for (var i = 0; i < buckets; i++)
                values[i] = latest;

            return new MeterTimeSeries(Catalog.Name, kind, bucketStarts, values, currentSnapshot, p50, p99);
        }

        // For each output bucket, sum (counter/updown) or mean (gauge/histogram)
        // every ring entry whose start falls inside the output bucket.
        var sums = new double[buckets];
        var weights = new int[buckets];

        for (var i = 0; i < ringSnapshot.Length; i++)
        {
            var b = ringSnapshot[i];
            if (b.SampleCount == 0 && b.Aggregate == 0d && b.BucketStart == default)
                continue;
            if (b.BucketStart < windowStart || b.BucketStart > now)
                continue;

            var idx = (int)((b.BucketStart - windowStart).Ticks / outBucketSize.Ticks);
            if (idx < 0) idx = 0;
            if (idx >= buckets) idx = buckets - 1;

            switch (kind)
            {
                case MeterKind.Counter:
                case MeterKind.UpDownCounter:
                    sums[idx] += b.Aggregate;
                    weights[idx] += Math.Max(1, b.SampleCount);
                    break;
                case MeterKind.Gauge:
                case MeterKind.Histogram:
                    // Mean-of-means weighted by sample count so multiple ring
                    // entries falling into one output bucket combine sensibly.
                    sums[idx] += b.Aggregate * b.SampleCount;
                    weights[idx] += b.SampleCount;
                    break;
            }
        }

        for (var i = 0; i < buckets; i++)
        {
            if (kind == MeterKind.Counter || kind == MeterKind.UpDownCounter)
            {
                values[i] = sums[i];
            }
            else
            {
                values[i] = weights[i] > 0 ? sums[i] / weights[i] : 0d;
            }
        }

        return new MeterTimeSeries(Catalog.Name, kind, bucketStarts, values, currentSnapshot, p50, p99);
    }

    // -------------------- internal helpers --------------------

    /// <summary>
    ///     Updates the recent-bucket ring with the given measurement, allocating
    ///     a new bucket slot if the timestamp falls outside the current head
    ///     bucket. MUST be called with <see cref="_gate" /> held.
    /// </summary>
    private void PushBucketAggregate(DateTimeOffset now, double value, bool signed, bool sample)
    {
        // signed=true => Counter / UpDownCounter: aggregate is sum of deltas
        // sample=true => Gauge / Histogram: aggregate is running mean over SampleCount

        var width = _options.BucketWidth;
        if (width <= TimeSpan.Zero) width = TimeSpan.FromSeconds(1);

        var bucketStart = AlignToBucket(now, width);

        if (_bucketCount == 0)
        {
            _buckets[_bucketHead] = sample
                ? new Bucket(bucketStart, value, 1)
                : new Bucket(bucketStart, value, 1);
            _bucketHead = (_bucketHead + 1) % _buckets.Length;
            _bucketCount = 1;
            return;
        }

        // Find current "head" bucket -- the most recently written slot is at
        // _bucketHead - 1 (mod length).
        var lastIdx = (_bucketHead - 1 + _buckets.Length) % _buckets.Length;
        var lastBucket = _buckets[lastIdx];

        if (lastBucket.BucketStart == bucketStart)
        {
            // Aggregate into the existing head bucket.
            if (sample)
            {
                var newCount = lastBucket.SampleCount + 1;
                var newMean = ((lastBucket.Aggregate * lastBucket.SampleCount) + value) / newCount;
                _buckets[lastIdx] = new Bucket(bucketStart, newMean, newCount);
            }
            else
            {
                _buckets[lastIdx] = new Bucket(
                    bucketStart,
                    lastBucket.Aggregate + value,
                    lastBucket.SampleCount + 1);
            }
            return;
        }

        // New bucket: advance the ring, possibly evicting the oldest.
        _buckets[_bucketHead] = sample
            ? new Bucket(bucketStart, value, 1)
            : new Bucket(bucketStart, value, 1);
        _bucketHead = (_bucketHead + 1) % _buckets.Length;
        if (_bucketCount < _buckets.Length) _bucketCount++;
    }

    private static DateTimeOffset AlignToBucket(DateTimeOffset now, TimeSpan width)
    {
        var widthTicks = width.Ticks;
        var aligned = (now.UtcTicks / widthTicks) * widthTicks;
        return new DateTimeOffset(aligned, TimeSpan.Zero);
    }

    private void PushReservoir(double value)
    {
        if (_reservoir is null) return;
        _reservoir[_reservoirHead] = value;
        _reservoirHead = (_reservoirHead + 1) % _reservoir.Length;
        if (_reservoirCount < _reservoir.Length) _reservoirCount++;
    }

    /// <summary>
    ///     Computes P50 / P99 from the bounded reservoir. MUST be called with
    ///     <see cref="_gate" /> held.
    /// </summary>
    private (double? p50, double? p99) ComputePercentilesLocked()
    {
        if (_reservoir is null || _reservoirCount == 0)
            return (0d, 0d);

        var copy = new double[_reservoirCount];
        Array.Copy(_reservoir, copy, _reservoirCount);
        Array.Sort(copy);

        return (Percentile(copy, 0.50), Percentile(copy, 0.99));
    }

    private static double Percentile(double[] sorted, double p)
    {
        if (sorted.Length == 0) return 0d;
        if (sorted.Length == 1) return sorted[0];
        var rank = p * (sorted.Length - 1);
        var lower = (int)Math.Floor(rank);
        var upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];
        var weight = rank - lower;
        return sorted[lower] * (1 - weight) + sorted[upper] * weight;
    }

    private double FindMostRecentBucket(Bucket[] snapshot)
    {
        var best = DateTimeOffset.MinValue;
        var bestValue = 0d;
        for (var i = 0; i < snapshot.Length; i++)
        {
            var b = snapshot[i];
            if (b.BucketStart > best)
            {
                best = b.BucketStart;
                bestValue = b.Aggregate;
            }
        }
        return bestValue;
    }

    /// <summary>
    ///     A single entry in the recent-bucket ring. Stored by value so we can
    ///     overwrite slots atomically under the gate without further allocation.
    /// </summary>
    private readonly record struct Bucket(DateTimeOffset BucketStart, double Aggregate, int SampleCount);
}
