using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Pins the contract <c>HitsPerPeriodChartletBuilder</c> exposes to the
///     Traffic page. The builder reads the same <see cref="ProjectedVisitor"/>
///     projection feeding the rest of the page (no new data store) and emits a
///     <c>ChartletViewModel</c> the shared <c>&lt;vc:sb-chartlet&gt;</c> partial
///     can render as a stacked bar with a click-to-drill on bot family.
/// </summary>
public sealed class HitsPerPeriodChartletBuilderTests
{
    [Fact]
    public void Build_emits_stacked_bar_with_drill_on_bot_type()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            new ProjectedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true,  Hits = 5, LastSeen = now.AddMinutes(-1) },
            new ProjectedVisitor { PrimarySignature = "b", BotType = null,      IsBot = false, Hits = 3, LastSeen = now.AddMinutes(-1) },
            new ProjectedVisitor { PrimarySignature = "c", BotType = "Scraper", IsBot = true,  Hits = 2, LastSeen = now.AddMinutes(-2) }
        };

        var model = HitsPerPeriodChartletBuilder.Build(rows, window: "1h");

        Assert.Equal(ChartletKind.StackedBar, model.Kind);
        Assert.NotNull(model.Drill);
        Assert.Equal("bot_type", model.Drill!.ParamKey);
        Assert.Equal("/dashboard/traffic", model.Drill.Url);
        Assert.Equal("#traffic-panels", model.Drill.PanelTarget);
    }

    [Fact]
    public void Build_emits_one_series_per_family_including_human_and_suspicious()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            new ProjectedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true,  Hits = 5, LastSeen = now.AddMinutes(-1), BotProbability = 0.95 },
            new ProjectedVisitor { PrimarySignature = "b", BotType = null,      IsBot = false, Hits = 3, LastSeen = now.AddMinutes(-1), BotProbability = 0.1 }
        };

        var model = HitsPerPeriodChartletBuilder.Build(rows, window: "1h");
        var keys = model.Series.Select(s => s.Key).ToHashSet();

        Assert.Contains("Human", keys);
        Assert.Contains("Suspicious", keys);
        Assert.Contains("Scraper", keys);
    }

    [Fact]
    public void Build_bucket_count_matches_window()
    {
        var empty = Array.Empty<ProjectedVisitor>();

        // UX1: 6h / 12h / 24h target a constant 72-bucket density (5/10/20 min).
        // 15m / 1h / 7d still resolve for bookmarked URLs.
        Assert.Equal(15,  HitsPerPeriodChartletBuilder.Build(empty, "15m").BucketLabels.Count);
        Assert.Equal(60,  HitsPerPeriodChartletBuilder.Build(empty, "1h").BucketLabels.Count);
        Assert.Equal(60,  HitsPerPeriodChartletBuilder.Build(empty, "60m").BucketLabels.Count);
        Assert.Equal(72,  HitsPerPeriodChartletBuilder.Build(empty, "6h").BucketLabels.Count);
        Assert.Equal(72,  HitsPerPeriodChartletBuilder.Build(empty, "12h").BucketLabels.Count);
        Assert.Equal(72,  HitsPerPeriodChartletBuilder.Build(empty, "24h").BucketLabels.Count);
        Assert.Equal(168, HitsPerPeriodChartletBuilder.Build(empty, "7d").BucketLabels.Count);
    }

    [Fact]
    public void Build_uses_logarithmic_y_axis_so_small_series_arent_crushed()
    {
        // UX1: 96%-bot column squashed the 4% human slice to one pixel on
        // a linear scale; switching to log makes both visible. This pins
        // the YScale so a regression doesn't silently revert to linear.
        var model = HitsPerPeriodChartletBuilder.Build(Array.Empty<ProjectedVisitor>(), "6h");
        Assert.Equal("logarithmic", model.Axes.YScale);
    }

    [Fact]
    public void Build_marks_Internal_series_hidden_so_it_doesnt_dominate_the_chart()
    {
        // UX2: gateway self-probes (Internal) are the largest series by
        // volume because StyloBot monitors itself. Default-hiding lets
        // the operator see actual customer traffic on first paint.
        var model = HitsPerPeriodChartletBuilder.Build(Array.Empty<ProjectedVisitor>(), "6h");
        var internalSeries = model.Series.Single(s => s.Key == "Internal");
        Assert.True(internalSeries.Hidden, "Internal series must start hidden so the chart doesn't open dominated by self-probes.");

        // Every other series stays visible by default -- the Internal hide
        // shouldn't blank-slate the whole chart.
        foreach (var s in model.Series.Where(s => s.Key != "Internal"))
        {
            Assert.False(s.Hidden, $"Series '{s.Key}' should not be hidden by default.");
        }
    }

    [Fact]
    public void Build_series_buckets_align_with_label_count()
    {
        var rows = new[]
        {
            new ProjectedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true, Hits = 1, LastSeen = DateTime.UtcNow }
        };

        var model = HitsPerPeriodChartletBuilder.Build(rows, "1h");
        foreach (var s in model.Series)
        {
            Assert.Equal(model.BucketLabels.Count, s.Buckets.Count);
        }
    }

    [Fact]
    public void Build_visitor_with_bot_type_lands_in_that_family_not_in_human_bucket()
    {
        var now = DateTime.UtcNow;
        var rows = new[]
        {
            new ProjectedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true, Hits = 7, LastSeen = now }
        };

        var model = HitsPerPeriodChartletBuilder.Build(rows, "1h");
        var scraper = model.Series.Single(s => s.Key == "Scraper");
        var human = model.Series.Single(s => s.Key == "Human");

        Assert.Equal(7L, scraper.Buckets.Sum());
        Assert.Equal(0L, human.Buckets.Sum());
    }

    [Fact]
    public void Build_uses_dashboard_color_semantics_per_family()
    {
        var model = HitsPerPeriodChartletBuilder.Build(Array.Empty<ProjectedVisitor>(), "1h");

        // Per project_dashboard_color_semantics: human=success, suspicious=warning,
        // bot families=danger, search/good=info, unknown=uncertain/info.
        Assert.Equal("--sb-color-risk-verified",  model.Series.Single(s => s.Key == "Human").ColorToken);
        Assert.Equal("--sb-color-risk-elevated",  model.Series.Single(s => s.Key == "Suspicious").ColorToken);
        Assert.Equal("--sb-color-risk-veryhigh",  model.Series.Single(s => s.Key == "Scraper").ColorToken);
    }

    // ---------------------------------------------------------------- hero chart

    private static TrafficTimeseries Series(params (int Bot, int Human, int Total)[] rows)
    {
        var start = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        return new TrafficTimeseries(
            rows.Select((_, i) => start.AddMinutes(20 * i)).ToArray(),
            rows.Select(r => r.Human).ToArray(),
            rows.Select(_ => 0).ToArray(),
            rows.Select(r => r.Bot).ToArray());
    }

    [Fact]
    public void BuildFromSeries_emits_overlapping_area_with_two_audience_series()
    {
        var model = HitsPerPeriodChartletBuilder.BuildFromSeries(
            Series((Bot: 7, Human: 2, Total: 9), (Bot: 5, Human: 3, Total: 8)), "24h");

        Assert.Equal(ChartletKind.Area, model.Kind);
        Assert.Equal(new[] { "Human", "Bot" }, model.Series.Select(s => s.Key));
        Assert.Equal("Human / unknown", model.Series.Single(s => s.Key == "Human").Label);
        Assert.Equal("Bot", model.Series.Single(s => s.Key == "Bot").Label);
        Assert.Equal("--sb-chart-human", model.Series.Single(s => s.Key == "Human").ColorToken);
        Assert.Equal("--sb-chart-bot", model.Series.Single(s => s.Key == "Bot").ColorToken);
        // Two series only: no Suspicious band is added at this layer.
        Assert.Equal(2, model.Series.Count);
    }

    [Fact]
    public void BuildSeries_folds_human_as_total_minus_bot_not_the_reported_human_count()
    {
        // A producer that reports a stale/confidence-gated HumanCount must not make
        // the chart's two bands disagree with the request total: Human = max(0, Total-Bot).
        var start = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        var points = new[]
        {
            new DashboardTimeSeriesPoint
            {
                Timestamp = start, BotCount = 6, HumanCount = 100, TotalCount = 10
            }
        };

        var ts = HitsPerPeriodChartletBuilder.BuildSeries(
            points, start, start.AddMinutes(20), TimeSpan.FromMinutes(20));

        Assert.Equal(4, ts.Human.Sum());
        Assert.Equal(6, ts.Bot.Sum());
        Assert.Equal(10, ts.Human.Sum() + ts.Bot.Sum());
    }

    [Fact]
    public void Empty_range_renders_zero_series_and_traffic_specific_copy()
    {
        var start = new DateTime(2026, 10, 22, 10, 0, 0, DateTimeKind.Utc);
        var ts = HitsPerPeriodChartletBuilder.BuildSeries(
            Array.Empty<DashboardTimeSeriesPoint>(), start, start.AddMinutes(40), TimeSpan.FromMinutes(20));
        var model = HitsPerPeriodChartletBuilder.BuildFromSeries(ts, "24h");

        Assert.Equal(2, model.BucketLabels.Count);
        Assert.All(model.Series, s => Assert.All(s.Buckets, v => Assert.Equal(0, v)));

        // The chart's empty sentence is about traffic, never the endpoint breakdown.
        var text = Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic.HitsPerPeriodChartModel.NoTrafficText;
        Assert.Equal("No recorded traffic history in this range yet.", text);
        Assert.DoesNotContain("endpoint", text, StringComparison.OrdinalIgnoreCase);
    }
}
