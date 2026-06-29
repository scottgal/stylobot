using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Pins the contract <c>CountryChartletBuilder</c> exposes to the Traffic
///     page: the same <see cref="CountryRow"/> rows that fed the legacy
///     by-country card project into a horizontal-bar <see cref="ChartletViewModel"/>
///     with a click-to-drill on the controller's <c>country</c> filter.
/// </summary>
public sealed class CountryChartletBuilderTests
{
    [Fact]
    public void Build_returns_horizontal_bar_with_country_drill()
    {
        var rows = new[]
        {
            new CountryRow("GB", Hits: 762, BotShare: 0.04),
            new CountryRow("US", Hits: 59,  BotShare: 1.0)
        };

        var model = CountryChartletBuilder.Build(rows);

        Assert.Equal(ChartletKind.HorizontalBar, model.Kind);
        Assert.NotNull(model.Drill);
        Assert.Equal("country", model.Drill!.ParamKey);
        Assert.Equal("/dashboard/traffic", model.Drill.Url);
        Assert.Equal("#traffic-panels", model.Drill.PanelTarget);
    }

    [Fact]
    public void Build_preserves_row_order_for_bucket_labels_and_buckets()
    {
        var rows = new[]
        {
            new CountryRow("GB", Hits: 762, BotShare: 0.04),
            new CountryRow("US", Hits: 59,  BotShare: 1.0),
            new CountryRow("DE", Hits: 12,  BotShare: 0.2)
        };

        var model = CountryChartletBuilder.Build(rows);

        Assert.Equal(new[] { "GB", "US", "DE" }, model.BucketLabels.ToArray());
        var series = Assert.Single(model.Series);
        Assert.Equal(new[] { 762L, 59L, 12L }, series.Buckets.ToArray());
    }

    [Fact]
    public void Build_empty_rows_produces_zero_label_chart_with_drill_intact()
    {
        var model = CountryChartletBuilder.Build(Array.Empty<CountryRow>());

        Assert.Equal(ChartletKind.HorizontalBar, model.Kind);
        Assert.Empty(model.BucketLabels);
        var series = Assert.Single(model.Series);
        Assert.Empty(series.Buckets);
        Assert.NotNull(model.Drill);
    }

    [Fact]
    public void Build_axes_label_country_on_y_and_hits_on_x()
    {
        var model = CountryChartletBuilder.Build(Array.Empty<CountryRow>());

        Assert.Equal("country", model.Axes.YLabel);
        Assert.Equal("hits", model.Axes.XLabel);
        Assert.Equal("number", model.Axes.YFormat);
    }

    [Fact]
    public void Build_series_key_is_stable_token_not_per_country()
    {
        // Horizontal bar in Chart.js shares one colour across the whole series,
        // so the series key must be the stable "all" token -- the per-country
        // drill value comes from the BAR LABEL on click, not the series key.
        var rows = new[] { new CountryRow("GB", 100, 0) };

        var model = CountryChartletBuilder.Build(rows);

        var series = Assert.Single(model.Series);
        Assert.Equal("all", series.Key);
    }
}
