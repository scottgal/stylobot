using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Pins <see cref="SiteHealthChartletBuilder"/>'s projection of
///     <see cref="DegradationSnapshot"/> history into the two chartlet
///     shapes the Traffic page renders side-by-side: latency Line, errors
///     StackedArea.
/// </summary>
public class SiteHealthChartletBuilderTests
{
    [Fact]
    public void IsAllHealthy_returns_true_for_empty_history()
    {
        Assert.True(SiteHealthChartletBuilder.IsAllHealthy(Array.Empty<DegradationSnapshot>()));
    }

    [Fact]
    public void IsAllHealthy_returns_true_when_every_arm_is_zero()
    {
        var rows = new[]
        {
            Snapshot(DateTime.UtcNow, latency: 12),
            Snapshot(DateTime.UtcNow.AddMinutes(-1), latency: 10),
        };
        Assert.True(SiteHealthChartletBuilder.IsAllHealthy(rows));
    }

    [Fact]
    public void IsAllHealthy_returns_false_when_any_error_arm_breaches_floor()
    {
        var rows = new[]
        {
            Snapshot(DateTime.UtcNow, latency: 12, rate5xx: 0.05),
        };
        Assert.False(SiteHealthChartletBuilder.IsAllHealthy(rows));
    }

    [Fact]
    public void BuildLatency_emits_one_Line_series_with_bucket_aligned_axis()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            Snapshot(now.AddMinutes(-30), latency: 100),
            Snapshot(now.AddMinutes(-15), latency: 200),
        };

        var chart = SiteHealthChartletBuilder.BuildLatency(rows, "1h");

        Assert.Equal(ChartletKind.Line, chart.Kind);
        Assert.Single(chart.Series);
        Assert.Equal("p95", chart.Series[0].Key);
        Assert.Equal(60, chart.BucketLabels.Count);
        Assert.Equal(60, chart.Series[0].Buckets.Count);
        Assert.Equal("ms", chart.Axes.YFormat);
        // At least one bucket carries a non-zero latency.
        Assert.Contains(chart.Series[0].Buckets, b => b > 0);
    }

    [Fact]
    public void BuildErrors_emits_StackedArea_with_5xx_429_4xx_series_in_percent_units()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            Snapshot(now.AddMinutes(-10), rate5xx: 0.50, rate4xx: 0.20, rate429: 0.10),
        };

        var chart = SiteHealthChartletBuilder.BuildErrors(rows, "1h");

        Assert.Equal(ChartletKind.StackedArea, chart.Kind);
        Assert.Equal(3, chart.Series.Count);
        var keys = chart.Series.Select(s => s.Key).OrderBy(s => s).ToList();
        Assert.Equal(new[] { "429", "4xx", "5xx" }, keys);
        Assert.Equal("percent", chart.Axes.YFormat);
        // 5xx rate 0.50 -> 50% rounded.
        var fiveXx = chart.Series.Single(s => s.Key == "5xx");
        Assert.Contains(fiveXx.Buckets, b => b == 50);
    }

    [Fact]
    public void BuildErrors_bucket_axis_matches_BuildLatency_for_the_same_window()
    {
        var rows = Array.Empty<DegradationSnapshot>();
        // Two-charts-side-by-side: their bucket count + labels MUST line up
        // so the operator reads the latency spike at the same X position
        // as the error spike. Single source of truth = ResolveBucketShape.
        var latency = SiteHealthChartletBuilder.BuildLatency(rows, "24h");
        var errors = SiteHealthChartletBuilder.BuildErrors(rows, "24h");

        Assert.Equal(latency.BucketLabels.Count, errors.BucketLabels.Count);
    }

    [Fact]
    public void Drill_is_null_so_the_widget_does_not_steal_navigation()
    {
        // No drill: the operator already lands on the Traffic page when
        // they see the spike. A click-into would mean a dedicated incident
        // surface that does not exist yet.
        var rows = Array.Empty<DegradationSnapshot>();
        Assert.Null(SiteHealthChartletBuilder.BuildLatency(rows, "1h").Drill);
        Assert.Null(SiteHealthChartletBuilder.BuildErrors(rows, "1h").Drill);
    }

    private static DegradationSnapshot Snapshot(
        DateTime t,
        double latency = 0,
        double rate5xx = 0,
        double rate4xx = 0,
        double rate429 = 0,
        double rate404 = 0)
        => new(
            TimestampUtc: t,
            Latency5xxRate: rate5xx,
            Latency4xxRate: rate4xx,
            Latency429Rate: rate429,
            LatencyP50Ms: latency,
            LatencyP95Ms: latency,
            NotFoundRate: rate404);
}
