using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Pins the contract <c>BotTypeChartletBuilder</c> exposes: the same
///     <see cref="BotTypeRow"/> rows that fed the legacy by-bot-type card
///     project into a horizontal-bar <see cref="ChartletViewModel"/> with a
///     click-to-drill on the controller's <c>bot_type</c> filter.
/// </summary>
public sealed class BotTypeChartletBuilderTests
{
    [Fact]
    public void Build_returns_horizontal_bar_with_bot_type_drill()
    {
        var rows = new[]
        {
            new BotTypeRow("Scraper", Hits: 120),
            new BotTypeRow("SearchEngine", Hits: 33)
        };

        var model = BotTypeChartletBuilder.Build(rows);

        Assert.Equal(ChartletKind.HorizontalBar, model.Kind);
        Assert.NotNull(model.Drill);
        Assert.Equal("bot_type", model.Drill!.ParamKey);
        Assert.Equal("/dashboard/traffic", model.Drill.Url);
        Assert.Equal("#traffic-panels", model.Drill.PanelTarget);
    }

    [Fact]
    public void Build_preserves_row_order_for_bucket_labels_and_buckets()
    {
        var rows = new[]
        {
            new BotTypeRow("Scraper", Hits: 120),
            new BotTypeRow("SearchEngine", Hits: 33),
            new BotTypeRow("Internal", Hits: 7)
        };

        var model = BotTypeChartletBuilder.Build(rows);

        Assert.Equal(new[] { "Scraper", "SearchEngine", "Internal" }, model.BucketLabels.ToArray());
        var series = Assert.Single(model.Series);
        Assert.Equal(new[] { 120L, 33L, 7L }, series.Buckets.ToArray());
    }

    [Fact]
    public void Build_empty_rows_produces_zero_label_chart_with_drill_intact()
    {
        var model = BotTypeChartletBuilder.Build(Array.Empty<BotTypeRow>());

        Assert.Equal(ChartletKind.HorizontalBar, model.Kind);
        Assert.Empty(model.BucketLabels);
        var series = Assert.Single(model.Series);
        Assert.Empty(series.Buckets);
        Assert.NotNull(model.Drill);
    }

    [Fact]
    public void Build_axes_label_bot_type_on_y_and_hits_on_x()
    {
        var model = BotTypeChartletBuilder.Build(Array.Empty<BotTypeRow>());

        Assert.Equal("bot type", model.Axes.YLabel);
        Assert.Equal("hits", model.Axes.XLabel);
        Assert.Equal("number", model.Axes.YFormat);
    }
}
