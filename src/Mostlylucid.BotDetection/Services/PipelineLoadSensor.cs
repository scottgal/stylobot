namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Tracks the detection pipeline's request rate via an exponential moving average.
///     Background services use <see cref="GetAdaptiveInterval"/> and
///     <see cref="GetWorstOffenderCap"/> to self-throttle and prioritise
///     the highest-threat signatures when CPU is constrained.
/// </summary>
public sealed class PipelineLoadSensor : ILoadBandSource, IDisposable
{
    // EMA smoothing factor: 30% weight on the latest 1-second sample.
    private const double Alpha = 0.3;

    private readonly double _criticalRps;
    private readonly double _highRps;
    private readonly double _normalRps;
    private readonly Timer _ticker;

    private double _smoothedRps;
    private int _tickCount;

    public PipelineLoadSensor(
        double normalRps = 20,
        double highRps = 50,
        double criticalRps = 100)
    {
        _normalRps   = normalRps;
        _highRps     = highRps;
        _criticalRps = criticalRps;

        _ticker = new Timer(Tick, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    public double SmoothedRps => Volatile.Read(ref _smoothedRps);

    public LoadBand CurrentBand => SmoothedRps switch
    {
        var r when r >= _criticalRps => LoadBand.Critical,
        var r when r >= _highRps     => LoadBand.High,
        var r when r >= _normalRps   => LoadBand.Normal,
        _                            => LoadBand.Low
    };

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
    ///     Called from the hot request path -- must be lock-free and zero-allocation.
    /// </summary>
    public void RecordRequest() => Interlocked.Increment(ref _tickCount);

    /// <summary>
    ///     Returns a scaled interval for background services: longer under load.
    /// </summary>
    public TimeSpan GetAdaptiveInterval(TimeSpan baseInterval) =>
        TimeSpan.FromSeconds(baseInterval.TotalSeconds * LoadFactor);

    /// <summary>
    ///     Maximum number of signatures a background service should process in one
    ///     run when under load. Worst offenders (highest bot probability) fill this
    ///     budget first. Returns <c>null</c> when load is low (process all).
    /// </summary>
    public int? GetWorstOffenderCap(int totalSignatures) => CurrentBand switch
    {
        LoadBand.Critical => Math.Max(10, totalSignatures / 8),
        LoadBand.High     => Math.Max(20, totalSignatures / 4),
        LoadBand.Normal   => Math.Max(50, totalSignatures / 2),
        _                 => null
    };

    private void Tick(object? _)
    {
        var count = Interlocked.Exchange(ref _tickCount, 0);
        var prev = Volatile.Read(ref _smoothedRps);
        Interlocked.Exchange(ref _smoothedRps, Alpha * count + (1 - Alpha) * prev);
    }
}

public enum LoadBand { Low, Normal, High, Critical }
