namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Thresholds for the adaptive <see cref="PipelineLoadSensor"/>. All values
///     bind under <c>BotDetection:PipelineLoadSensor:*</c> so an operator can
///     retune the gateway's self-protection trip points without a rebuild.
///     <para>
///     The sensor mixes five inputs into the load band; whichever crosses its
///     threshold first wins:
///     <list type="bullet">
///       <item>Smoothed RPS (legacy axis, used during the ~50s baseline warmup
///         before adaptive axes have enough samples).</item>
///       <item>Detection-latency ratio (fast EMA over slow baseline). Default
///         2x = High, 5x = Critical -- scale-invariant.</item>
///       <item>Upstream RTT ratio (same shape).</item>
///       <item>Consecutive ticks with non-zero ThreadPool pending work.</item>
///       <item>Gen2 GC collections per second (sustained).</item>
///     </list>
///     The ratio-based axes are dimensionless so the same defaults work on a
///     Pi-class device and a 32-core server.
///     </para>
/// </summary>
public sealed class PipelineLoadSensorOptions
{
    // --- legacy RPS axis (used until adaptive baselines warm up) ---

    /// <summary>RPS floor for the Normal band. Default 20.</summary>
    public double NormalRps { get; set; } = 20;

    /// <summary>RPS floor for the High band. Default 50.</summary>
    public double HighRps { get; set; } = 50;

    /// <summary>RPS floor for the Critical band. Default 100.</summary>
    public double CriticalRps { get; set; } = 100;

    // --- adaptive ratio axes ---

    /// <summary>Detection-latency / upstream-RTT ratio that trips High. Default 2.0.</summary>
    public double HighRatio { get; set; } = 2.0;

    /// <summary>Detection-latency / upstream-RTT ratio that trips Critical. Default 5.0.</summary>
    public double CriticalRatio { get; set; } = 5.0;

    // --- ThreadPool starvation axis ---

    /// <summary>Consecutive 1-second ticks with pending ThreadPool work before High. Default 3.</summary>
    public int HighStarvedTicks { get; set; } = 3;

    /// <summary>Consecutive ticks before Critical. Default 6.</summary>
    public int CriticalStarvedTicks { get; set; } = 6;

    // --- Gen2 GC churn axis ---

    /// <summary>Gen2 collections/sec EMA that trips High. Default 1.0.</summary>
    public double HighGen2PerSec { get; set; } = 1.0;

    /// <summary>Gen2 collections/sec EMA that trips Critical. Default 2.0.</summary>
    public double CriticalGen2PerSec { get; set; } = 2.0;

    // --- baseline window ---

    /// <summary>
    ///     Number of one-second samples held in the latency / RTT baseline window.
    ///     The baseline is the <see cref="BaselinePercentile"/>-th value of this
    ///     window, recomputed on each tick. Larger windows resist transient
    ///     pressure spikes; smaller windows track real long-term drift faster.
    ///     Default 120 (2 minutes).
    /// </summary>
    public int BaselineWindowSamples { get; set; } = 120;

    /// <summary>
    ///     Percentile (0.0 - 1.0) of the baseline window used as the "healthy
    ///     steady state" floor. The fast end of recent reality, robust to a
    ///     single anomalously-fast warmup sample (the previous min-tracking
    ///     baseline locked on that sample and made every later request read
    ///     as 50-100x over baseline on low-traffic deployments). Default 0.10
    ///     (10th percentile).
    /// </summary>
    public double BaselinePercentile { get; set; } = 0.10;

    /// <summary>
    ///     Maximum fraction the baseline can drift UPWARD per tick once the
    ///     warmup window has filled. Stops a sustained pressure event from
    ///     washing the baseline up to match itself (which would mask the
    ///     pressure). Downward movement is unrestricted -- genuine speedups
    ///     are tracked immediately. Default 0.001 (0.1% per tick = ~12 minute
    ///     half-life for real diurnal drift).
    /// </summary>
    public double BaselineUpwardDriftPerTick { get; set; } = 0.001;
}
