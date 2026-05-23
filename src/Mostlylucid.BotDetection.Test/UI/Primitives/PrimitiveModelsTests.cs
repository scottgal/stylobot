using Mostlylucid.BotDetection.UI.Models.Primitives;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI.Primitives;

public class PrimitiveModelsTests
{
    [Fact]
    public void ThreatIconModel_defaults_are_safe()
    {
        var m = new ThreatIconModel(Band: null, BotProbability: 0.0);
        Assert.Null(m.Band);
        Assert.Equal(0.0, m.BotProbability);
    }

    [Fact]
    public void IntentIconModel_preserves_intent_string()
    {
        var m = new IntentIconModel(Intent: "Scraper");
        Assert.Equal("Scraper", m.Intent);
    }

    [Fact]
    public void SparklineModel_empty_arrays_have_correct_shape()
    {
        var m = new SparklineModel(BotTrend: System.Array.Empty<int>(),
                                   HumanTrend: System.Array.Empty<int>(),
                                   WindowMinutes: 60);
        Assert.Empty(m.BotTrend);
        Assert.Empty(m.HumanTrend);
        Assert.Equal(60, m.WindowMinutes);
    }

    [Fact]
    public void TableToolbarModel_accepts_chip_list()
    {
        var chips = new[]
        {
            new FilterChip(Key: "all", Label: "All", Count: 12, Url: "/?filter=all"),
            new FilterChip(Key: "bots", Label: "Bots", Count: 4, Url: "/?filter=bots"),
        };
        var windows = new[]
        {
            new TimeWindowOption(Key: "1h", Label: "1h", Url: "/?window=1h"),
            new TimeWindowOption(Key: "6h", Label: "6h", Url: "/?window=6h"),
        };
        var m = new TableToolbarModel(
            TargetId: "live-activity-list",
            Chips: chips,
            ActiveFilter: "all",
            ShowSearch: true,
            SearchUrl: "/?",
            TimeWindowOptions: windows,
            ActiveTimeWindow: "1h");
        Assert.Equal(2, m.Chips.Count);
        Assert.Equal("all", m.ActiveFilter);
        Assert.Equal(2, m.TimeWindowOptions!.Count);
    }
}