using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Adaptive multi-signal pressure detection in <see cref="PipelineLoadSensor"/>.
///     Uses the internal <c>TickOnce</c> hook to drive the 1-second timer
///     deterministically; never sleeps. Each test establishes a baseline by
///     ticking >= <c>BaselineWarmupTicks</c> (50) times at quiet load, then
///     applies the pressure signal under test and asserts the band moves.
/// </summary>
public sealed class PipelineLoadSensorTests
{
    private const int Warmup = 60; // > BaselineWarmupTicks (50)

    private static PipelineLoadSensor New(
        double highRatio = 2.0,
        double criticalRatio = 5.0,
        // Suppress ThreadPool / GC inputs by default: xUnit and the test
        // process are noisy on both and would dominate the assertion. Tests
        // that exercise those signals pass their own values.
        int highStarvedTicks = int.MaxValue,
        int criticalStarvedTicks = int.MaxValue,
        double highGen2 = 1e9,
        double criticalGen2 = 1e9)
        => new(
            normalRps: 1e9, highRps: 1e9, criticalRps: 1e9,   // RPS knobs effectively off
            highRatio, criticalRatio,
            highStarvedTicks, criticalStarvedTicks,
            highGen2, criticalGen2);

    private static void WarmupLatencyBaseline(PipelineLoadSensor s, double baselineMs)
    {
        // Drive `Warmup` ticks of quiet latency to seed the baseline EMA. After
        // this, _latencyBaselineSamples >= 50 and the adaptive path engages.
        for (var i = 0; i < Warmup; i++)
        {
            s.RecordDetectionLatency(baselineMs);
            s.TickOnce();
        }
    }

    private static void WarmupDeviationBaseline(PipelineLoadSensor s, double ratio = 1.0)
    {
        // Drive Warmup ticks of neutral deviation (ratio 1.0 = on-baseline) to
        // seed _rttBaselineSamples >= BaselineWarmupTicks so the adaptive path engages.
        for (var i = 0; i < Warmup; i++)
        {
            s.RecordUpstreamDeviation(ratio);
            s.TickOnce();
        }
    }

    [Fact]
    public void BeforeWarmup_FallsBackToRpsBands()
    {
        // No latency/deviation samples recorded yet; band falls through to legacy
        // RPS thresholds. Constructor sets high RPS thresholds, so band stays Low.
        var s = New();
        Assert.Equal(LoadBand.Low, s.CurrentBand);
    }

    [Fact]
    public void Low_AtBaselineLatency()
    {
        var s = New();
        WarmupLatencyBaseline(s, baselineMs: 10);
        Assert.Equal(LoadBand.Low, s.CurrentBand);
    }

    [Fact]
    public void Normal_AtModestLatencyDrift()
    {
        var s = New();
        WarmupLatencyBaseline(s, baselineMs: 10);
        // Drive a few ticks at 1.5x baseline so the fast EMA settles above 1.3x
        for (var i = 0; i < 10; i++)
        {
            s.RecordDetectionLatency(15);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Normal, s.CurrentBand);
    }

    [Fact]
    public void High_AtTwoXLatencyDrift()
    {
        var s = New();
        WarmupLatencyBaseline(s, baselineMs: 10);
        // 2x baseline sustained => High via the highRatio = 2.0 default
        for (var i = 0; i < 10; i++)
        {
            s.RecordDetectionLatency(25);   // safely above 2x, accounting for EMA
            s.TickOnce();
        }
        Assert.Equal(LoadBand.High, s.CurrentBand);
    }

    [Fact]
    public void Critical_AtFiveXLatencyDrift()
    {
        var s = New();
        WarmupLatencyBaseline(s, baselineMs: 10);
        for (var i = 0; i < 10; i++)
        {
            s.RecordDetectionLatency(80);   // > 5x baseline
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Critical, s.CurrentBand);
    }

    [Fact]
    public void High_AtSustainedUpstreamDeviationDrift()
    {
        var s = New(highRatio: 2.0, criticalRatio: 5.0);
        WarmupDeviationBaseline(s, ratio: 1.0);
        for (var i = 0; i < 10; i++)
        {
            s.RecordUpstreamDeviation(3.0);   // 3x over baseline (1.0), highRatio = 2.0
            s.TickOnce();
        }
        Assert.Equal(LoadBand.High, s.CurrentBand);
    }

    [Fact]
    public void HighestBandWins_LatencyHigh_DeviationCritical()
    {
        var s = New(highRatio: 2.0, criticalRatio: 5.0);
        WarmupLatencyBaseline(s, baselineMs: 10);
        WarmupDeviationBaseline(s, ratio: 1.0);
        // Lat 2x (High), deviation 6x (Critical) => Critical wins.
        for (var i = 0; i < 10; i++)
        {
            s.RecordDetectionLatency(25);
            s.RecordUpstreamDeviation(6.0);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Critical, s.CurrentBand);
    }

    [Fact]
    public void BandDecays_WhenPressureRecovers()
    {
        var s = New();
        WarmupLatencyBaseline(s, baselineMs: 10);
        // Push into Critical
        for (var i = 0; i < 10; i++)
        {
            s.RecordDetectionLatency(80);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Critical, s.CurrentBand);

        // Latency drops back to baseline; band decays as the fast EMA chases the
        // new measurement. Within ~20 ticks the EMA is back at baseline level.
        for (var i = 0; i < 25; i++)
        {
            s.RecordDetectionLatency(10);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Low, s.CurrentBand);
    }

    [Fact]
    public void BaselineDoesNotChase_SustainedPressure()
    {
        // If the baseline tracked the fast EMA freely, a sustained pressure
        // event would wash the baseline upward and the ratio would converge to
        // 1.0, masking the pressure. We cap baseline upward movement at 5%/tick
        // so a sustained ratio over multiple minutes still reads as High.
        var s = New();
        WarmupLatencyBaseline(s, baselineMs: 10);
        // Pressure: 5x baseline for 200 ticks (= 200 seconds simulated)
        for (var i = 0; i < 200; i++)
        {
            s.RecordDetectionLatency(50);
            s.TickOnce();
        }
        // Should still be Critical (or at minimum High); not washed back to Low.
        Assert.NotEqual(LoadBand.Low, s.CurrentBand);
        Assert.NotEqual(LoadBand.Normal, s.CurrentBand);
    }

    [Fact]
    public void Options_default_MinSamplesForTrustedBaseline_is_30()
    {
        var opts = new PipelineLoadSensorOptions();
        Assert.Equal(30, opts.MinSamplesForTrustedBaseline);
    }

    [Fact]
    public void Options_default_BaselineRefreshInterval_is_one_minute()
    {
        var opts = new PipelineLoadSensorOptions();
        Assert.Equal(TimeSpan.FromMinutes(1), opts.BaselineRefreshInterval);
    }

    [Fact]
    public void Upstream_deviation_at_one_keeps_band_low()
    {
        var s = New();
        for (var i = 0; i < 60; i++)
        {
            s.RecordUpstreamDeviation(1.0);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Low, s.CurrentBand);
    }

    [Fact]
    public void Upstream_deviation_at_threshold_high_fires_High()
    {
        var s = New(highRatio: 2.0, criticalRatio: 5.0);
        for (var i = 0; i < 60; i++)
        {
            s.RecordUpstreamDeviation(2.5);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.High, s.CurrentBand);
    }

    [Fact]
    public void Upstream_deviation_at_threshold_critical_fires_Critical()
    {
        var s = New(highRatio: 2.0, criticalRatio: 5.0);
        for (var i = 0; i < 60; i++)
        {
            s.RecordUpstreamDeviation(6.0);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Critical, s.CurrentBand);
    }
}
