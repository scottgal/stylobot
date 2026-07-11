using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Adaptive pipeline pressure sensor. <see cref="CurrentBand"/> derives from
///     signals the gateway can observe about itself and its upstream, not from
///     a hardcoded RPS threshold. The same code therefore reports Low on a
///     server idling at 1000 RPS and High on a Pi being slammed at 30 RPS,
///     because the band reflects ACTUAL pressure (ThreadPool starvation, GC
///     churn, detection-latency drift, upstream deviation) rather than an
///     absolute traffic level.
///
///     Inputs (all updated every 1-second tick):
///     <list type="bullet">
///       <item><b>RPS EMA</b>. Kept for backward compat with the legacy
///         <see cref="PipelineLoadSensor(double,double,double)"/> constructor
///         and as the fallback band-source during baseline warmup.
///         Not load-classifying on its own once baselines exist.</item>
///       <item><b>Detection-latency EMA</b>. Exponential moving average of
///         <see cref="RecordDetectionLatency"/> samples. The band fires High when
///         recent samples are <c>HighRatio</c> over the slow baseline, Critical
///         at <c>CriticalRatio</c>.</item>
///       <item><b>Upstream deviation EMA</b>. EMA of the per-request deviation
///         ratio fed by <see cref="RecordUpstreamDeviation"/>. Ratio 1.0 means
///         the request hit its endpoint's own normal. Band fires when the EMA
///         crosses <c>HighRatio</c> or <c>CriticalRatio</c> directly (no
///         per-axis rolling baseline; implicit baseline is 1.0).</item>
///       <item><b>ThreadPool starvation</b>. Each tick checks
///         <see cref="ThreadPool.PendingWorkItemCount"/>; >= <c>HighStarvedTicks</c>
///         consecutive non-zero readings fires High, >=
///         <c>CriticalStarvedTicks</c> fires Critical.</item>
///       <item><b>Gen2 GC rate</b>. Sustained Gen2 collections (>= <c>HighGen2PerSec</c>
///         averaged) indicates we're allocating too fast for the heap and is a
///         High signal; >= <c>CriticalGen2PerSec</c> is Critical.</item>
///     </list>
///
///     The highest band any input reports wins. Bands decay back down naturally
///     as the EMAs catch up.
///
///     Backward compat: when only the RPS-threshold constructor is used (no
///     latency/deviation samples ever recorded), the sensor falls through to the
///     classic RPS-only bands.
///
///     Background services consume <see cref="GetAdaptiveInterval"/> and
///     <see cref="GetWorstOffenderCap"/> to self-throttle when the band rises.
///     The middleware consumes <see cref="LoadShedDecision.ShouldShed"/> to
///     skip detection for a per-policy fraction of requests.
/// </summary>
public sealed class PipelineLoadSensor : ILoadBandSource, IDisposable
{
    // EMA smoothing factor: 30% weight on the latest 1-second sample.
    private const double Alpha = 0.3;
    // Number of baseline samples to collect before adaptive logic engages; below
    // this we fall back to the legacy RPS bands. ~50s of real measurements.
    private const int BaselineWarmupTicks = 50;
    // A ratio above this (over baseline) on either latency axis is sufficient to
    // trigger the Normal band even before High thresholds.
    private const double NormalRatio = 1.3;

    // --- legacy RPS knobs (used during warmup and when no adaptive samples flow) ---
    private readonly double _criticalRps;
    private readonly double _highRps;
    private readonly double _normalRps;

    // --- adaptive thresholds (ratio + counter form, so absolute scale is irrelevant) ---
    private readonly double _highRatio;
    private readonly double _criticalRatio;
    private readonly double _maxRecordedDeviationRatio;
    private readonly int _highStarvedTicks;
    private readonly int _criticalStarvedTicks;
    private readonly double _highGen2PerSec;
    private readonly double _criticalGen2PerSec;

    // --- baseline window (detection-latency axis only; deviation axis has implicit baseline 1.0) ---
    private readonly double[] _latencyWindow;
    private readonly int _baselineWindowSize;
    private readonly double _baselinePercentile;
    private readonly double _baselineUpwardDriftPerTick;
    private int _latencyWindowWriteIdx;
    private int _latencyWindowFilled;
    private readonly object _latencyWindowLock = new();

    private readonly Timer _ticker;

    // RPS tracking (still exposed for backward compat)
    private double _smoothedRps;
    private int _tickCount;

    // Detection-latency tracking (microseconds; double-promoted from ms on record).
    private long _latencyAccumUs;
    private int _latencySampleCount;
    private double _latencyEmaUs;
    private double _latencyBaselineUs;
    private int _latencyBaselineSamples;

    // Upstream-deviation tracking (EMA of ratio; implicit baseline 1.0).
    // _upstreamDeviationAccumScaled is the per-tick sum of (ratio * 1_000_000)
    // so the accumulator can stay in long range; recovered to a dimensionless
    // mean ratio inside Tick(). _upstreamDeviationRatioEma is the EMA of those
    // per-tick mean ratios (NOT microseconds; renamed from the pre-2026-06-25
    // _rttEmaUs to remove the confusing unit-suffix that produced multiple
    // wrong root-cause diagnoses against this file).
    private long _upstreamDeviationAccumScaled;
    private int _upstreamDeviationSampleCount;
    private double _upstreamDeviationRatioEma;
    private int _upstreamDeviationTicksWithSamples;

    // ThreadPool starvation.
    private int _consecutiveStarvedTicks;

    // Gen2 GC rate.
    private int _lastGen2Count;
    private double _gen2PerSecondEma;

    /// <summary>
    ///     Legacy RPS-only constructor. Retained so existing call sites
    ///     (incl. <c>BlackboardOrchestratorLoadShedTests</c>) keep compiling.
    ///     Adaptive thresholds default to the values used by the adaptive
    ///     constructor; latency/deviation inputs are no-ops if never called.
    /// </summary>
    public PipelineLoadSensor(
        double normalRps = 20,
        double highRps = 50,
        double criticalRps = 100)
        : this(
            normalRps, highRps, criticalRps,
            highRatio: 2.0, criticalRatio: 5.0,
            highStarvedTicks: 3, criticalStarvedTicks: 6,
            highGen2PerSec: 1.0, criticalGen2PerSec: 2.0,
            baselineWindowSamples: 120, baselinePercentile: 0.10,
            baselineUpwardDriftPerTick: 0.001,
            maxRecordedDeviationRatio: 10.0)
    { }

    /// <summary>
    ///     Full adaptive constructor. All thresholds expressed as
    ///     dimensionless ratios (over slow baseline) or counter floors, so the
    ///     same defaults are correct on a Pi and on a 32-core server.
    /// </summary>
    public PipelineLoadSensor(
        double normalRps,
        double highRps,
        double criticalRps,
        double highRatio,
        double criticalRatio,
        int highStarvedTicks,
        int criticalStarvedTicks,
        double highGen2PerSec,
        double criticalGen2PerSec,
        int baselineWindowSamples = 120,
        double baselinePercentile = 0.10,
        double baselineUpwardDriftPerTick = 0.001,
        double maxRecordedDeviationRatio = 10.0)
    {
        _normalRps                  = normalRps;
        _highRps                    = highRps;
        _criticalRps                = criticalRps;
        _highRatio                  = highRatio;
        _criticalRatio              = criticalRatio;
        _highStarvedTicks           = highStarvedTicks;
        _criticalStarvedTicks       = criticalStarvedTicks;
        _highGen2PerSec             = highGen2PerSec;
        _criticalGen2PerSec         = criticalGen2PerSec;
        // Guard: a max-recorded clamp below the Critical ratio would cap the
        // EMA below Critical and the sensor could never trip Critical from the
        // deviation axis at all. Force it >= criticalRatio.
        _maxRecordedDeviationRatio  = Math.Max(maxRecordedDeviationRatio, criticalRatio);

        _baselineWindowSize          = Math.Max(8, baselineWindowSamples);
        _baselinePercentile          = Math.Clamp(baselinePercentile, 0.0, 0.5);
        _baselineUpwardDriftPerTick  = Math.Max(0.0, baselineUpwardDriftPerTick);
        _latencyWindow               = new double[_baselineWindowSize];

        _lastGen2Count = GC.CollectionCount(2);

        _ticker = new Timer(Tick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public double SmoothedRps => Volatile.Read(ref _smoothedRps);

    /// <summary>
    ///     Current detection-latency EMA divided by min-baseline. Returns 0
    ///     when no baseline established yet. Ratio above HighRatio/CriticalRatio
    ///     contributes to the band escalation.
    /// </summary>
    public double DetectionLatencyRatio
    {
        get
        {
            var baseline = Volatile.Read(ref _latencyBaselineUs);
            return baseline <= 0 ? 0 : Volatile.Read(ref _latencyEmaUs) / baseline;
        }
    }

    /// <summary>
    ///     Current EMA of the per-request deviation ratio fed by
    ///     <see cref="RecordUpstreamDeviation"/>. Returns 0 before the
    ///     first sample. Band escalates when this value crosses the
    ///     configured High / Critical ratios.
    /// </summary>
    public double UpstreamDeviationEma => Volatile.Read(ref _upstreamDeviationRatioEma);

    /// <summary>
    ///     Consecutive 1-second ticks at which ThreadPool.PendingWorkItemCount
    ///     was non-zero. Higher = worker starvation.
    /// </summary>
    public int ThreadPoolStarvedTicks => Volatile.Read(ref _consecutiveStarvedTicks);

    /// <summary>EMA of Gen2 GC collections per second.</summary>
    public double Gen2PerSecond => Volatile.Read(ref _gen2PerSecondEma);

    /// <summary>
    ///     Adaptive band. The first input to fire Critical short-circuits;
    ///     otherwise the highest band across all inputs wins. During baseline
    ///     warmup, falls back to the legacy RPS bands.
    /// </summary>
    public LoadBand CurrentBand
    {
        get
        {
            // No latency/deviation input has been wired yet, OR not enough samples
            // collected to trust the baseline. Fall back to the RPS bands; the
            // adaptive layer only takes over once baselines stabilise.
            var latencyReady = Volatile.Read(ref _latencyBaselineSamples) >= BaselineWarmupTicks;
            var rttReady     = Volatile.Read(ref _upstreamDeviationTicksWithSamples) >= BaselineWarmupTicks;
            if (!latencyReady && !rttReady)
            {
                var rps = Volatile.Read(ref _smoothedRps);
                if (rps >= _criticalRps) return LoadBand.Critical;
                if (rps >= _highRps)     return LoadBand.High;
                if (rps >= _normalRps)   return LoadBand.Normal;
                return LoadBand.Low;
            }

            var band = LoadBand.Low;

            if (latencyReady)
            {
                var baseUs = Volatile.Read(ref _latencyBaselineUs);
                if (baseUs > 0)
                {
                    var ratio = Volatile.Read(ref _latencyEmaUs) / baseUs;
                    if (ratio >= _criticalRatio) return LoadBand.Critical;
                    if (ratio >= _highRatio)     band = Worse(band, LoadBand.High);
                    else if (ratio >= NormalRatio) band = Worse(band, LoadBand.Normal);
                }
            }

            if (rttReady)
            {
                // _upstreamDeviationRatioEma holds the EMA of per-tick mean
                // deviation ratios (dimensionless; pre-2026-06-25 this field was
                // microseconds — the rename eliminates the unit confusion that
                // produced repeated wrong root-cause diagnoses).
                var ratio = Volatile.Read(ref _upstreamDeviationRatioEma);
                if (ratio >= _criticalRatio) return LoadBand.Critical;
                if (ratio >= _highRatio)     band = Worse(band, LoadBand.High);
                else if (ratio >= NormalRatio) band = Worse(band, LoadBand.Normal);
            }

            var starved = Volatile.Read(ref _consecutiveStarvedTicks);
            if (starved >= _criticalStarvedTicks) return LoadBand.Critical;
            if (starved >= _highStarvedTicks)     band = Worse(band, LoadBand.High);

            var gen2 = Volatile.Read(ref _gen2PerSecondEma);
            if (gen2 >= _criticalGen2PerSec) return LoadBand.Critical;
            if (gen2 >= _highGen2PerSec)     band = Worse(band, LoadBand.High);

            return band;
        }
    }

    /// <summary>
    ///     Scale factor applied to background service intervals.
    ///     Returns 1.0 at low traffic; up to 4.0 at critical.
    /// </summary>
    public double LoadFactor => CurrentBand switch
    {
        LoadBand.Critical => 4.0,
        LoadBand.High     => 2.5,
        LoadBand.Normal   => 1.5,
        _                 => 1.0
    };

    public void Dispose() => _ticker.Dispose();

    /// <summary>
    ///     Called from the hot request path. Lock-free, zero-alloc.
    /// </summary>
    public void RecordRequest() => Interlocked.Increment(ref _tickCount);

    /// <summary>
    ///     Records a detection-pipeline processing time, in milliseconds.
    ///     Lock-free. The 1-second tick rolls samples into EMA + baseline.
    /// </summary>
    public void RecordDetectionLatency(double ms)
    {
        if (ms <= 0 || double.IsNaN(ms) || double.IsInfinity(ms)) return;
        Interlocked.Add(ref _latencyAccumUs, (long)(ms * 1000.0));
        Interlocked.Increment(ref _latencySampleCount);
    }

    /// <summary>
    ///     Records the post-detection-deviation ratio for one completed
    ///     request, where <paramref name="ratio"/> is
    ///     <c>actualUpstreamMs / expectedMsFromIEndpointPerfBaseline</c>.
    ///     1.0 means the request hit its endpoint's own normal; 2.0 means
    ///     twice the endpoint's p95; etc. Unknown endpoints (no baseline)
    ///     should pass 1.0 so they contribute a neutral sample.
    ///     <para>
    ///     Replaces the pre-2026-06-25 <c>RecordUpstreamRtt(double ms)</c>
    ///     which fed absolute milliseconds into a global rolling-window
    ///     baseline. That design tripped Critical on hosts that serve both
    ///     fast static assets and slow dashboard pages (different intrinsic
    ///     latencies cannot share one baseline). The deviation ratio
    ///     normalises against each endpoint's own normal, so a mixed
    ///     workload stays in Low band by default.
    ///     </para>
    /// </summary>
    public void RecordUpstreamDeviation(double ratio)
    {
        if (ratio <= 0 || double.IsNaN(ratio) || double.IsInfinity(ratio)) return;
        // Hard-clamp each recorded sample so a single outlier ratio (e.g. 50ms
        // upstream against a sub-ms endpoint baseline = ratio 100) cannot peg
        // the EMA above CriticalRatio for tens of ticks. The clamp doesn't
        // suppress real sustained pressure (every sample still contributes,
        // and a sustained burst above _maxRecordedDeviationRatio still drives
        // the EMA toward the clamp and trips Critical) — it just stops one
        // bad sample from holding the band in Critical long after the burst
        // ends. Without this clamp an idle gateway whose dashboard reads
        // briefly spiked stays wedged in Critical and sheds 100% of inbound
        // traffic via LoadShedDecision.ShouldShed.
        if (ratio > _maxRecordedDeviationRatio) ratio = _maxRecordedDeviationRatio;
        Interlocked.Add(ref _upstreamDeviationAccumScaled, (long)(ratio * 1_000_000.0));  // scaled so the accumulator stays in long range
        Interlocked.Increment(ref _upstreamDeviationSampleCount);
    }

    /// <summary>
    ///     Returns a scaled interval for background services: longer under load.
    /// </summary>
    public TimeSpan GetAdaptiveInterval(TimeSpan baseInterval) =>
        TimeSpan.FromSeconds(baseInterval.TotalSeconds * LoadFactor);

    /// <summary>
    ///     Maximum number of signatures a background service should process in
    ///     one run when under load. Worst offenders (highest bot probability)
    ///     fill this budget first. Returns <c>null</c> when load is low.
    /// </summary>
    public int? GetWorstOffenderCap(int totalSignatures) => CurrentBand switch
    {
        LoadBand.Critical => Math.Max(10, totalSignatures / 8),
        LoadBand.High     => Math.Max(20, totalSignatures / 4),
        LoadBand.Normal   => Math.Max(50, totalSignatures / 2),
        _                 => null
    };

    /// <summary>
    ///     Internal test hook. Drives one tick synchronously so unit tests can
    ///     advance the EMA/baseline state without sleeping for the Timer to fire.
    ///     Not for production code paths.
    /// </summary>
    internal void TickOnce() => Tick(null);

    private static LoadBand Worse(LoadBand a, LoadBand b) =>
        (LoadBand)Math.Max((int)a, (int)b);

    private void Tick(object? _)
    {
        // ---- RPS EMA ----
        var count = Interlocked.Exchange(ref _tickCount, 0);
        var prevRps = Volatile.Read(ref _smoothedRps);
        Interlocked.Exchange(ref _smoothedRps, Ewma.Update(prevRps, count, Alpha));

        // ---- Detection latency: fast EMA + min-tracking baseline ----
        var latAccum = Interlocked.Exchange(ref _latencyAccumUs, 0);
        var latCount = Interlocked.Exchange(ref _latencySampleCount, 0);
        if (latCount > 0)
        {
            var meanUs = (double)latAccum / latCount;
            var prevLat = Volatile.Read(ref _latencyEmaUs);
            var newLat = Ewma.Update(prevLat, meanUs, Alpha);
            Interlocked.Exchange(ref _latencyEmaUs, newLat);

            // Baseline is the configured percentile of a rolling window
            // (default 10th of last 120 samples). The fast end of recent
            // reality, robust to one anomalously-fast warmup sample. The
            // pre-2026-06-24 implementation locked the baseline to the
            // all-time min, which on low-traffic deployments captured the
            // first ultra-fast warmup sample (~0.1ms) and made every
            // subsequent real request read as 50-100x over baseline, tripping
            // LoadBand.Critical and refusing traffic with 503. The percentile
            // window can drift up when the workload genuinely shifts, so
            // operators no longer need a process restart to recover.
            var prevLatBase = Volatile.Read(ref _latencyBaselineUs);
            var latWarmedUp = Volatile.Read(ref _latencyBaselineSamples) >= BaselineWarmupTicks;
            var newBase = UpdateRollingBaseline(
                _latencyWindow, _latencyWindowLock,
                ref _latencyWindowWriteIdx, ref _latencyWindowFilled,
                meanUs, _baselinePercentile,
                prevLatBase, latWarmedUp, _baselineUpwardDriftPerTick);
            Interlocked.Exchange(ref _latencyBaselineUs, newBase);
            Interlocked.Increment(ref _latencyBaselineSamples);
        }

        // ---- Upstream deviation EMA (replaces the old rolling-baseline path) ----
        // _upstreamDeviationAccumScaled / _upstreamDeviationSampleCount produces
        // the mean ratio for the tick; the EMA smooths it; band selection (above)
        // compares the EMA ratio directly to HighRatio / CriticalRatio. Baseline
        // is implicitly 1.0 because the input is already normalised by the
        // per-endpoint p95 from IEndpointPerfBaseline at the call site, and
        // individual samples are clamped at _maxRecordedDeviationRatio so a
        // single outlier cannot peg the EMA.
        var rttAccum = Interlocked.Exchange(ref _upstreamDeviationAccumScaled, 0);
        var rttCount = Interlocked.Exchange(ref _upstreamDeviationSampleCount, 0);
        if (rttCount > 0)
        {
            // Recover the ratio from the scaled accumulator (see RecordUpstreamDeviation).
            var meanRatio = (rttAccum / 1_000_000.0) / rttCount;
            var prevEma = Volatile.Read(ref _upstreamDeviationRatioEma);
            Interlocked.Exchange(ref _upstreamDeviationRatioEma, Ewma.Update(prevEma, meanRatio, Alpha));
            // Count ticks that contained any sample so the band selector knows
            // when the axis has enough history to engage. Renamed from
            // _rttBaselineSamples (which misleadingly suggested baseline tracking;
            // this axis has no rolling baseline — its baseline is the implicit 1.0).
            Interlocked.Increment(ref _upstreamDeviationTicksWithSamples);
        }

        // ---- ThreadPool starvation counter ----
        if (ThreadPool.PendingWorkItemCount > 0)
            Interlocked.Increment(ref _consecutiveStarvedTicks);
        else
            Interlocked.Exchange(ref _consecutiveStarvedTicks, 0);

        // ---- Gen2 GC rate (per-second EMA) ----
        var gen2Now = GC.CollectionCount(2);
        var gen2Delta = gen2Now - _lastGen2Count;
        _lastGen2Count = gen2Now;
        var prevGen2 = Volatile.Read(ref _gen2PerSecondEma);
        Interlocked.Exchange(ref _gen2PerSecondEma, Ewma.Update(prevGen2, gen2Delta, Alpha));
    }

    /// <summary>
    ///     Writes one sample into the rolling window and returns the new
    ///     baseline. During warmup (filled &lt; <c>BaselineWarmupTicks</c>) the
    ///     baseline tracks the percentile freely so a bad initial outlier
    ///     gets washed out within ~50 ticks. After warmup the baseline can
    ///     drop freely (genuine improvements are tracked immediately) but
    ///     upward movement is capped at <paramref name="upwardDriftPerTick"/>
    ///     so a sustained pressure event cannot wash the baseline up to
    ///     match itself.
    /// </summary>
    private static double UpdateRollingBaseline(
        double[] window,
        object gate,
        ref int writeIdx,
        ref int filled,
        double sampleUs,
        double percentile,
        double prevBaseline,
        bool warmedUp,
        double upwardDriftPerTick)
    {
        double candidate;
        lock (gate)
        {
            window[writeIdx] = sampleUs;
            writeIdx = (writeIdx + 1) % window.Length;
            if (filled < window.Length) filled++;
            var snapshot = new double[filled];
            Array.Copy(window, snapshot, filled);
            Array.Sort(snapshot);
            var idx = (int)Math.Floor(percentile * (filled - 1));
            candidate = snapshot[Math.Clamp(idx, 0, filled - 1)];
        }

        if (!warmedUp || prevBaseline <= 0) return candidate;
        if (candidate <= prevBaseline) return candidate;
        var cap = prevBaseline * (1.0 + upwardDriftPerTick);
        return Math.Min(candidate, cap);
    }
}

public enum LoadBand { Low, Normal, High, Critical }
