using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Adaptive pipeline pressure sensor. <see cref="CurrentBand"/> derives from
///     signals the gateway can observe about itself and its upstream, not from
///     a hardcoded RPS threshold. The same code therefore reports Low on a
///     server idling at 1000 RPS and High on a Pi being slammed at 30 RPS,
///     because the band reflects ACTUAL pressure (ThreadPool starvation, GC
///     churn, detection-latency drift, upstream-RTT drift) rather than an
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
///       <item><b>Upstream-RTT EMA</b>. Same shape, fed by
///         <see cref="RecordUpstreamRtt"/> from the middleware's <c>_next</c>
///         timing.</item>
///       <item><b>ThreadPool starvation</b>. Each tick checks
///         <see cref="ThreadPool.PendingWorkItemCount"/>; ≥ <c>HighStarvedTicks</c>
///         consecutive non-zero readings fires High, ≥
///         <c>CriticalStarvedTicks</c> fires Critical.</item>
///       <item><b>Gen2 GC rate</b>. Sustained Gen2 collections (≥ <c>HighGen2PerSec</c>
///         averaged) indicates we're allocating too fast for the heap and is a
///         High signal; ≥ <c>CriticalGen2PerSec</c> is Critical.</item>
///     </list>
///
///     The highest band any input reports wins. Bands decay back down naturally
///     as the EMAs catch up.
///
///     Backward compat: when only the RPS-threshold constructor is used (no
///     latency/RTT samples ever recorded), the sensor falls through to the
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
    // Slow EMA for baseline: ~50-sample memory so the baseline doesn't chase a
    // sustained pressure event and mask it.
    private const double BaselineAlpha = 0.02;
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
    private readonly int _highStarvedTicks;
    private readonly int _criticalStarvedTicks;
    private readonly double _highGen2PerSec;
    private readonly double _criticalGen2PerSec;

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

    // Upstream-RTT tracking.
    private long _rttAccumUs;
    private int _rttSampleCount;
    private double _rttEmaUs;
    private double _rttBaselineUs;
    private int _rttBaselineSamples;

    // ThreadPool starvation.
    private int _consecutiveStarvedTicks;

    // Gen2 GC rate.
    private int _lastGen2Count;
    private double _gen2PerSecondEma;

    /// <summary>
    ///     Legacy RPS-only constructor. Retained so existing call sites
    ///     (incl. <c>BlackboardOrchestratorLoadShedTests</c>) keep compiling.
    ///     Adaptive thresholds default to the values used by the adaptive
    ///     constructor; latency/RTT inputs are no-ops if never called.
    /// </summary>
    public PipelineLoadSensor(
        double normalRps = 20,
        double highRps = 50,
        double criticalRps = 100)
        : this(
            normalRps, highRps, criticalRps,
            highRatio: 2.0, criticalRatio: 5.0,
            highStarvedTicks: 3, criticalStarvedTicks: 6,
            highGen2PerSec: 1.0, criticalGen2PerSec: 2.0)
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
        double criticalGen2PerSec)
    {
        _normalRps             = normalRps;
        _highRps               = highRps;
        _criticalRps           = criticalRps;
        _highRatio             = highRatio;
        _criticalRatio         = criticalRatio;
        _highStarvedTicks      = highStarvedTicks;
        _criticalStarvedTicks  = criticalStarvedTicks;
        _highGen2PerSec        = highGen2PerSec;
        _criticalGen2PerSec    = criticalGen2PerSec;

        _lastGen2Count = GC.CollectionCount(2);

        _ticker = new Timer(Tick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public double SmoothedRps => Volatile.Read(ref _smoothedRps);

    /// <summary>
    ///     Adaptive band. The first input to fire Critical short-circuits;
    ///     otherwise the highest band across all inputs wins. During baseline
    ///     warmup, falls back to the legacy RPS bands.
    /// </summary>
    public LoadBand CurrentBand
    {
        get
        {
            // No latency/RTT input has been wired yet, OR not enough samples
            // collected to trust the baseline. Fall back to the RPS bands; the
            // adaptive layer only takes over once baselines stabilise.
            var latencyReady = Volatile.Read(ref _latencyBaselineSamples) >= BaselineWarmupTicks;
            var rttReady     = Volatile.Read(ref _rttBaselineSamples) >= BaselineWarmupTicks;
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
                var baseUs = Volatile.Read(ref _rttBaselineUs);
                if (baseUs > 0)
                {
                    var ratio = Volatile.Read(ref _rttEmaUs) / baseUs;
                    if (ratio >= _criticalRatio) return LoadBand.Critical;
                    if (ratio >= _highRatio)     band = Worse(band, LoadBand.High);
                    else if (ratio >= NormalRatio) band = Worse(band, LoadBand.Normal);
                }
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
    ///     Records an upstream proxy RTT, in milliseconds (typically the
    ///     wall-clock time the middleware spent inside <c>_next(context)</c>).
    ///     Lock-free.
    /// </summary>
    public void RecordUpstreamRtt(double ms)
    {
        if (ms <= 0 || double.IsNaN(ms) || double.IsInfinity(ms)) return;
        Interlocked.Add(ref _rttAccumUs, (long)(ms * 1000.0));
        Interlocked.Increment(ref _rttSampleCount);
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

        // ---- Detection latency: fast EMA + slow baseline ----
        var latAccum = Interlocked.Exchange(ref _latencyAccumUs, 0);
        var latCount = Interlocked.Exchange(ref _latencySampleCount, 0);
        if (latCount > 0)
        {
            var meanUs = (double)latAccum / latCount;
            var prevLat = Volatile.Read(ref _latencyEmaUs);
            var newLat = Ewma.Update(prevLat, meanUs, Alpha);
            Interlocked.Exchange(ref _latencyEmaUs, newLat);

            // Baseline initialisation: on the first sample, seed the baseline
            // to the observed mean directly so the ratio starts at 1.0 instead
            // of effectively infinite. After that, apply slow EMA with an
            // upward cap of 5%/tick so a sustained pressure spike can't wash
            // the baseline up and mask the pressure it's tracking.
            var prevBase = Volatile.Read(ref _latencyBaselineUs);
            double newBase;
            if (prevBase == 0)
            {
                newBase = meanUs;
            }
            else
            {
                var sample = Math.Min(newLat, prevBase * 1.05);
                newBase = Ewma.Update(prevBase, sample, BaselineAlpha);
            }
            Interlocked.Exchange(ref _latencyBaselineUs, newBase);
            Interlocked.Increment(ref _latencyBaselineSamples);
        }

        // ---- Upstream RTT: same shape ----
        var rttAccum = Interlocked.Exchange(ref _rttAccumUs, 0);
        var rttCount = Interlocked.Exchange(ref _rttSampleCount, 0);
        if (rttCount > 0)
        {
            var meanUs = (double)rttAccum / rttCount;
            var prevRtt = Volatile.Read(ref _rttEmaUs);
            var newRtt = Ewma.Update(prevRtt, meanUs, Alpha);
            Interlocked.Exchange(ref _rttEmaUs, newRtt);

            var prevBase = Volatile.Read(ref _rttBaselineUs);
            double newBase;
            if (prevBase == 0)
            {
                newBase = meanUs;
            }
            else
            {
                var sample = Math.Min(newRtt, prevBase * 1.05);
                newBase = Ewma.Update(prevBase, sample, BaselineAlpha);
            }
            Interlocked.Exchange(ref _rttBaselineUs, newBase);
            Interlocked.Increment(ref _rttBaselineSamples);
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
}

public enum LoadBand { Low, Normal, High, Critical }
