using FluentAssertions;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Dashboard cohesive-charts plan (Task A4). Real bot traffic was hidden
///     behind a "ThreatBand is Medium/High/Critical" filter that, after the
///     parasitic header source was ripped in #178, only Internal pings ever
///     satisfied. The widened filter surfaces high-probability bots even when
///     ThreatBand is "None" (the common case for Scraper/Tool/Unknown rows
///     classified by the FOSS pipeline today).
/// </summary>
public class ThreatsFilterTests
{
    private static ProjectedVisitor Row(double prob, string? band, string? botType = "Scraper")
        => new()
        {
            PrimarySignature = $"sig-{prob:F2}-{band ?? "null"}",
            BotProbability = prob,
            ThreatBand = band,
            BotType = botType,
            LastSeen = DateTime.UtcNow
        };

    [Fact]
    public void Apply_includes_high_probability_bots_below_critical()
    {
        var rows = new[]
        {
            Row(0.95, "None"),
            Row(0.45, "None")
        };

        var threats = ThreatsFilter.Apply(rows, new ThreatsOptions { LowBotProbabilityFloor = 0.8 });

        threats.Should().HaveCount(1);
        threats[0].BotProbability.Should().Be(0.95);
    }

    [Fact]
    public void Apply_prioritises_critical_then_high_then_high_probability()
    {
        var rows = new[]
        {
            Row(0.85, "None"),
            Row(0.60, "Critical", botType: "Internal"),
            Row(0.92, "High", botType: "Tool")
        };

        var threats = ThreatsFilter.Apply(rows, new ThreatsOptions { LowBotProbabilityFloor = 0.8 });

        threats.Should().HaveCount(3);
        threats[0].ThreatBand.Should().Be("Critical");
        threats[1].ThreatBand.Should().Be("High");
        threats[2].BotProbability.Should().Be(0.85);
        threats[2].ThreatBand.Should().Be("None");
    }

    [Fact]
    public void Apply_respects_TopN()
    {
        var rows = Enumerable.Range(0, 20)
            .Select(_ => Row(0.9, "None"))
            .ToList();

        var threats = ThreatsFilter.Apply(rows, new ThreatsOptions { LowBotProbabilityFloor = 0.8, TopN = 5 });

        threats.Should().HaveCount(5);
    }

    [Fact]
    public void Apply_orders_critical_above_high_above_medium_above_high_probability_none()
    {
        // Same probability across rows so ordering must come from band-rank
        // alone (Critical > High > Medium > None). Catches a regression where
        // the secondary tiebreak (BotProbability desc) accidentally bubbles a
        // 0.99 "None" above a 0.99 "Critical".
        var rows = new[]
        {
            Row(0.99, "None"),
            Row(0.99, "Medium"),
            Row(0.99, "High"),
            Row(0.99, "Critical")
        };

        var threats = ThreatsFilter.Apply(rows, new ThreatsOptions { LowBotProbabilityFloor = 0.8 });

        threats.Should().HaveCount(4);
        threats[0].ThreatBand.Should().Be("Critical");
        threats[1].ThreatBand.Should().Be("High");
        threats[2].ThreatBand.Should().Be("Medium");
        threats[3].ThreatBand.Should().Be("None");
    }

    [Fact]
    public void Apply_excludes_low_probability_none_rows()
    {
        // Floor at 0.8: rows below the floor with no severe band are dropped
        // entirely. Today these would clutter the panel with humans.
        var rows = new[]
        {
            Row(0.10, "None"),
            Row(0.30, null),
            Row(0.79, "None"),
            Row(0.80, "None")  // boundary: >= floor must be included
        };

        var threats = ThreatsFilter.Apply(rows, new ThreatsOptions { LowBotProbabilityFloor = 0.8 });

        threats.Should().HaveCount(1);
        threats[0].BotProbability.Should().Be(0.80);
    }
}
