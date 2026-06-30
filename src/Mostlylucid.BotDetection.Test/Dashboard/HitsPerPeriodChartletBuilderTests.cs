using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Pins the contract <c>HitsPerPeriodChartletBuilder</c> exposes to the
///     Traffic page. The builder reads the same <see cref="CachedVisitor"/>
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
            new CachedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true,  Hits = 5, LastSeen = now.AddMinutes(-1) },
            new CachedVisitor { PrimarySignature = "b", BotType = null,      IsBot = false, Hits = 3, LastSeen = now.AddMinutes(-1) },
            new CachedVisitor { PrimarySignature = "c", BotType = "Scraper", IsBot = true,  Hits = 2, LastSeen = now.AddMinutes(-2) }
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
            new CachedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true,  Hits = 5, LastSeen = now.AddMinutes(-1), BotProbability = 0.95 },
            new CachedVisitor { PrimarySignature = "b", BotType = null,      IsBot = false, Hits = 3, LastSeen = now.AddMinutes(-1), BotProbability = 0.1 }
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
        var empty = Array.Empty<CachedVisitor>();

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
        var model = HitsPerPeriodChartletBuilder.Build(Array.Empty<CachedVisitor>(), "6h");
        Assert.Equal("logarithmic", model.Axes.YScale);
    }

    [Fact]
    public void Build_marks_Internal_series_hidden_so_it_doesnt_dominate_the_chart()
    {
        // UX2: gateway self-probes (Internal) are the largest series by
        // volume because StyloBot monitors itself. Default-hiding lets
        // the operator see actual customer traffic on first paint.
        var model = HitsPerPeriodChartletBuilder.Build(Array.Empty<CachedVisitor>(), "6h");
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
            new CachedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true, Hits = 1, LastSeen = DateTime.UtcNow }
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
            new CachedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true, Hits = 7, LastSeen = now }
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
        var model = HitsPerPeriodChartletBuilder.Build(Array.Empty<CachedVisitor>(), "1h");

        // Per project_dashboard_color_semantics: human=success, suspicious=warning,
        // bot families=danger, search/good=info, unknown=uncertain/info.
        Assert.Equal("--sb-color-risk-verified",  model.Series.Single(s => s.Key == "Human").ColorToken);
        Assert.Equal("--sb-color-risk-elevated",  model.Series.Single(s => s.Key == "Suspicious").ColorToken);
        Assert.Equal("--sb-color-risk-veryhigh",  model.Series.Single(s => s.Key == "Scraper").ColorToken);
    }
}
