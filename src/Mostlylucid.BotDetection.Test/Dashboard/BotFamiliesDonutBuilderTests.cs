using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Pins the contract <c>BotFamiliesDonutBuilder</c> exposes: a donut over
///     visitor projections with one slice per family, using the same family
///     table + colour mapping as the headline stacked bar so the two visuals
///     stay in lock-step.
/// </summary>
public sealed class BotFamiliesDonutBuilderTests
{
    [Fact]
    public void Build_donut_with_drill_on_bot_type()
    {
        var visitors = new[]
        {
            new CachedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true,  Hits = 5, BotProbability = 0.95 },
            new CachedVisitor { PrimarySignature = "b", BotType = "GoodBot", IsBot = true,  Hits = 3, BotProbability = 0.9  },
            new CachedVisitor { PrimarySignature = "c", BotType = null,      IsBot = false, Hits = 1, BotProbability = 0.05 }
        };

        var model = BotFamiliesDonutBuilder.Build(visitors);

        Assert.Equal(ChartletKind.Donut, model.Kind);
        Assert.NotNull(model.Drill);
        Assert.Equal("bot_type", model.Drill!.ParamKey);
        Assert.Equal("/dashboard/traffic", model.Drill.Url);
        Assert.Equal("#traffic-panels", model.Drill.PanelTarget);
    }

    [Fact]
    public void Build_emits_one_series_per_known_family()
    {
        var model = BotFamiliesDonutBuilder.Build(Array.Empty<CachedVisitor>());

        var keys = model.Series.Select(s => s.Key).ToHashSet();
        Assert.Contains("Human", keys);
        Assert.Contains("Suspicious", keys);
        Assert.Contains("Scraper", keys);
        Assert.Contains("SearchEngine", keys);
        Assert.Contains("GoodBot", keys);
        Assert.Contains("Tool", keys);
        Assert.Contains("Unknown", keys);
        Assert.Contains("Internal", keys);
    }

    [Fact]
    public void Build_visitor_with_bot_type_lands_in_that_family()
    {
        var visitors = new[]
        {
            new CachedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true, Hits = 7, BotProbability = 0.95 }
        };

        var model = BotFamiliesDonutBuilder.Build(visitors);

        var scraper = model.Series.Single(s => s.Key == "Scraper");
        var human = model.Series.Single(s => s.Key == "Human");
        Assert.Equal(7L, scraper.Buckets.Single());
        Assert.Equal(0L, human.Buckets.Single());
    }

    [Fact]
    public void Build_low_probability_lands_in_human_family()
    {
        var visitors = new[]
        {
            new CachedVisitor { PrimarySignature = "a", IsBot = false, Hits = 10, BotProbability = 0.05 }
        };

        var model = BotFamiliesDonutBuilder.Build(visitors);

        Assert.Equal(10L, model.Series.Single(s => s.Key == "Human").Buckets.Single());
        Assert.Equal(0L,  model.Series.Single(s => s.Key == "Suspicious").Buckets.Single());
    }

    [Fact]
    public void Build_uses_dashboard_color_semantics_per_family()
    {
        var model = BotFamiliesDonutBuilder.Build(Array.Empty<CachedVisitor>());

        // Same mapping as HitsPerPeriodChartletBuilder so the headline stacked
        // bar and this donut stay visually coherent.
        Assert.Equal("--sb-color-risk-verified",  model.Series.Single(s => s.Key == "Human").ColorToken);
        Assert.Equal("--sb-color-risk-elevated",  model.Series.Single(s => s.Key == "Suspicious").ColorToken);
        Assert.Equal("--sb-color-risk-veryhigh",  model.Series.Single(s => s.Key == "Scraper").ColorToken);
        Assert.Equal("--sb-color-risk-verylow",   model.Series.Single(s => s.Key == "GoodBot").ColorToken);
    }

    [Fact]
    public void Build_donut_has_single_bucket_per_series()
    {
        var visitors = new[]
        {
            new CachedVisitor { PrimarySignature = "a", BotType = "Scraper", IsBot = true, Hits = 3, BotProbability = 0.95 }
        };

        var model = BotFamiliesDonutBuilder.Build(visitors);

        foreach (var s in model.Series)
        {
            Assert.Single(s.Buckets);
        }
    }
}
