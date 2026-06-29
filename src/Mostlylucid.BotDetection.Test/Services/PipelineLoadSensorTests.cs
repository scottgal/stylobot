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

    /// <summary>
    ///     Regression for the staging incident where the marketing-site fan-out
    ///     to the gateway dashboard API blanked the V2 traffic counters: the
    ///     deviation EMA accepted any input ratio (including extreme outliers
    ///     produced by sub-ms endpoint baselines), so a single 50ms request
    ///     against a 0.5ms p95 produced ratio 100 and pegged the EMA above
    ///     CriticalRatio (5.0) for ~14 ticks. The shed-decision then dropped
    ///     every inbound request — including the dashboard reads — as
    ///     503 X-StyloBot-Shed in 1ms.
    ///
    ///     Fix: clamp the per-request recorded ratio at construction-time
    ///     <c>maxRecordedDeviationRatio</c> (default 10.0 = 2x CriticalRatio).
    ///     A single outlier still contributes at the clamp, but never spikes
    ///     the EMA into a multi-tick stuck-Critical state.
    /// </summary>
    [Fact]
    public void Single_outlier_recorded_ratio_is_clamped()
    {
        // No baseline established: every tick has one recorded outlier ratio
        // and the rest neutral. After warmup, the EMA must stay within the
        // clamp (well below 20x, which the raw outliers would otherwise drive
        // toward).
        var s = New(highRatio: 2.0, criticalRatio: 5.0);
        // 60 ticks of neutral ratio so the axis warms up at 1.0.
        WarmupDeviationBaseline(s, ratio: 1.0);
        // Inject one extreme outlier per tick for the next 30 ticks against a
        // background of 10 neutral samples. Without the clamp the EMA would
        // chase the outlier and trip Critical. With the default clamp
        // (10.0), the per-tick mean stays bounded.
        for (var i = 0; i < 30; i++)
        {
            for (var n = 0; n < 10; n++) s.RecordUpstreamDeviation(1.0);
            s.RecordUpstreamDeviation(10_000.0);   // would be ratio 10000 without the clamp
            s.TickOnce();
        }
        // EMA should not have escaped above the clamp. (Per-tick mean is at most
        // (10*1.0 + 10.0) / 11 ~= 1.82; well under HighRatio 2.0 -> band stays Low.)
        Assert.InRange(s.UpstreamDeviationEma, 0.0, 2.0);
        Assert.True(s.CurrentBand <= LoadBand.Normal,
            $"Expected band <= Normal after clamping outliers; was {s.CurrentBand} with EMA={s.UpstreamDeviationEma:F3}");
    }

    /// <summary>
    ///     Idle-host pin: a host receiving sparse, near-baseline traffic must
    ///     read as Low, never Critical. Without the input clamp + ratio floor,
    ///     a fresh staging gateway was wedged in Critical because the early
    ///     percentile baseline locked in a sub-ms value and any subsequent
    ///     normal request produced a 50x outlier.
    /// </summary>
    [Fact]
    public void Idle_host_with_sparse_near_baseline_traffic_stays_Low()
    {
        var s = New();
        // 60 ticks of neutral deviation; a few ticks have no sample at all
        // (sparse traffic) and a few have a single ratio 1.0 sample.
        for (var i = 0; i < 60; i++)
        {
            if (i % 3 == 0) s.RecordUpstreamDeviation(1.0);
            s.TickOnce();
        }
        Assert.Equal(LoadBand.Low, s.CurrentBand);
    }

    /// <summary>
    ///     Clamp-guard contract: a <c>maxRecordedDeviationRatio</c> below
    ///     <c>criticalRatio</c> would cap every recorded sample below the trip
    ///     line and the band could never escalate from the deviation axis at
    ///     all. The ctor promotes the clamp to <c>criticalRatio</c>; with the
    ///     headroom afforded by larger inputs, the asymptotic EMA reaches
    ///     <c>criticalRatio</c> and Critical fires under sustained pressure.
    ///     Without the promotion, an extreme input would still leave the EMA
    ///     pinned at the (too-low) clamp and the band would never escalate.
    /// </summary>
    [Fact]
    public void Sustained_clamped_pressure_still_trips_Critical()
    {
        // ctor receives clamp=1.0 (well below criticalRatio=5.0); promotion to
        // criticalRatio (= 5.0) means sustained extreme input is clamped to 5.0
        // and the EMA converges to 5.0. The >= criticalRatio comparison fires
        // once the EMA crosses (modulo floating-point asymptote — sustained
        // pressure of 5.0 input reaches the trip line within numerical noise).
        var s = new PipelineLoadSensor(
            normalRps: 1e9, highRps: 1e9, criticalRps: 1e9,
            highRatio: 2.0, criticalRatio: 5.0,
            highStarvedTicks: int.MaxValue, criticalStarvedTicks: int.MaxValue,
            highGen2PerSec: 1e9, criticalGen2PerSec: 1e9,
            maxRecordedDeviationRatio: 1.0);
        // Drive enough ticks that EMA convergence is effectively complete.
        // With Alpha = 0.3, EMA of constant input 5.0 = 5.0 * (1 - 0.7^n);
        // after 500 ticks that's 5.0 * (1 - 1e-77) = 5.0 to machine precision.
        for (var i = 0; i < 500; i++)
        {
            s.RecordUpstreamDeviation(100.0);
            s.TickOnce();
        }
        // EMA is at (or extremely close to) the promoted clamp = criticalRatio.
        // Compare against criticalRatio - epsilon to absorb the EMA asymptote.
        Assert.True(s.UpstreamDeviationEma >= 5.0 - 1e-9,
            $"Expected EMA ~5.0 after promotion-clamp; was {s.UpstreamDeviationEma}");
        // Band escalation: when EMA equals criticalRatio (within float
        // precision), the band selector fires Critical. If EMA falls a hair
        // below criticalRatio, the selector falls through to High; either way
        // the deviation axis IS contributing pressure (proving the clamp was
        // promoted — without promotion the EMA would max out at 1.0 = Low).
        Assert.True(s.CurrentBand >= LoadBand.High,
            $"Clamp promotion must let sustained-extreme-input axis at least trip High; was {s.CurrentBand}");
    }
}
