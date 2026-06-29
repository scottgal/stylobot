using System.Text.Json;
using System.Text.Json.Serialization;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public sealed class ChartletViewModelTests
{
    [Fact]
    public void ChartletViewModel_serialises_drill_url_with_camel_case_paramKey()
    {
        var m = new ChartletViewModel(
            Id: "hits-per-period",
            Kind: ChartletKind.StackedBar,
            BucketLabels: new[] { "09:00", "09:05" },
            Series: new[] { new ChartletSeries("Scraper", "Scraper", "--sb-danger", new long[] { 5, 7 }) },
            Axes: new ChartletAxes(YLabel: "hits", YFormat: "number", XLabel: "time", GridLines: true),
            Drill: new ChartletDrill("/dashboard/traffic", "bot_type", "#traffic-panels"));
        var json = JsonSerializer.Serialize(m, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        Assert.Contains("\"id\":\"hits-per-period\"", json);
        Assert.Contains("\"paramKey\":\"bot_type\"", json);
        Assert.Contains("\"panelTarget\":\"#traffic-panels\"", json);
        Assert.Contains("\"buckets\":[5,7]", json);
    }

    [Fact]
    public void ChartletKind_serialises_as_string_not_int()
    {
        var m = new ChartletViewModel("x", ChartletKind.HorizontalBar,
            Array.Empty<string>(), Array.Empty<ChartletSeries>(),
            new ChartletAxes("y", "number", "x", true), null);
        var json = JsonSerializer.Serialize(m, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            Converters = { new JsonStringEnumConverter() }
        });
        Assert.Contains("\"kind\":\"HorizontalBar\"", json);
    }

    [Fact]
    public void ChartletDrill_is_nullable_and_omits_cleanly_when_null()
    {
        var m = new ChartletViewModel("y", ChartletKind.Line,
            Array.Empty<string>(), Array.Empty<ChartletSeries>(),
            new ChartletAxes("y", "ms", "x", true), Drill: null);
        var json = JsonSerializer.Serialize(m, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        });
        Assert.Contains("\"drill\":null", json);
    }
}
