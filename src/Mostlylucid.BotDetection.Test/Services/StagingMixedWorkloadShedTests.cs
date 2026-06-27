using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Regression: the 2026-06-25 staging incident where the website host
///     served both fast static assets (about 10ms) and a slower dashboard
///     URL (about 110ms). The pre-redesign sensor learned a global baseline
///     from the fast paths and read the dashboard URL as 11x over baseline,
///     tripping Critical and refusing 50% of requests with 503.
///
///     With the per-endpoint deviation axis, each endpoint contributes
///     ratio 1.0 (it is at its own normal) and the band stays Low. This
///     test simulates the exact traffic shape and asserts no band escalation.
/// </summary>
public sealed class StagingMixedWorkloadShedTests
{
    private sealed class FakeBaseline : IEndpointPerfBaseline
    {
        private readonly Dictionary<(string, string), double> _values = new()
        {
            { ("GET", "/img/{static}"), 10.0 },
            { ("GET", "/dashboard/entity/{slug}"), 110.0 },
        };

        public double GetExpectedMs(string method, string normalizedPath)
            => _values.TryGetValue((method, normalizedPath), out var v) ? v : 0.0;
    }

    [Fact]
    public void Mixed_workload_at_each_endpoints_own_normal_stays_in_low_band()
    {
        var sensor = new PipelineLoadSensor(
            normalRps: 1e9, highRps: 1e9, criticalRps: 1e9,
            highRatio: 2.0, criticalRatio: 5.0,
            highStarvedTicks: int.MaxValue, criticalStarvedTicks: int.MaxValue,
            highGen2PerSec: 1e9, criticalGen2PerSec: 1e9);
        var baseline = new FakeBaseline();

        // Simulate 60 ticks; per tick: 100 fast-static requests + 50 slow-dashboard
        // requests, each AT its own endpoint's normal p95. Ratio is 1.0 throughout.
        for (var tick = 0; tick < 60; tick++)
        {
            for (var i = 0; i < 100; i++)
            {
                var actualMs = 10.0;
                var expected = baseline.GetExpectedMs("GET", "/img/{static}");
                sensor.RecordUpstreamDeviation(actualMs / expected);
            }
            for (var i = 0; i < 50; i++)
            {
                var actualMs = 110.0;
                var expected = baseline.GetExpectedMs("GET", "/dashboard/entity/{slug}");
                sensor.RecordUpstreamDeviation(actualMs / expected);
            }
            sensor.TickOnce();
        }

        Assert.Equal(LoadBand.Low, sensor.CurrentBand);
    }

    [Fact]
    public void Genuine_systemwide_2x_slowdown_does_trip_high()
    {
        // Sanity check that the new axis still detects real pressure: every
        // endpoint runs at 2.5x its own p95 -> ratio averages 2.5 -> band
        // crosses HighRatio (2.0).
        var sensor = new PipelineLoadSensor(
            normalRps: 1e9, highRps: 1e9, criticalRps: 1e9,
            highRatio: 2.0, criticalRatio: 5.0,
            highStarvedTicks: int.MaxValue, criticalStarvedTicks: int.MaxValue,
            highGen2PerSec: 1e9, criticalGen2PerSec: 1e9);
        var baseline = new FakeBaseline();

        for (var tick = 0; tick < 60; tick++)
        {
            for (var i = 0; i < 100; i++)
            {
                sensor.RecordUpstreamDeviation(25.0 / baseline.GetExpectedMs("GET", "/img/{static}"));
            }
            for (var i = 0; i < 50; i++)
            {
                sensor.RecordUpstreamDeviation(275.0 / baseline.GetExpectedMs("GET", "/dashboard/entity/{slug}"));
            }
            sensor.TickOnce();
        }

        Assert.NotEqual(LoadBand.Low, sensor.CurrentBand);
        Assert.NotEqual(LoadBand.Normal, sensor.CurrentBand);
    }
}
