using FluentAssertions;
using Mostlylucid.BotDetection.UI.Helpers;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

public class AnalyticsRangeParserTests
{
    [Theory]
    [InlineData("1h",  60)]
    [InlineData("2h",  120)]
    [InlineData("6h",  6 * 60)]
    [InlineData("12h", 12 * 60)]
    [InlineData("24h", 24 * 60)]
    [InlineData("7d",  7 * 24 * 60)]
    [InlineData("14d", 14 * 24 * 60)]
    [InlineData("30d", 30 * 24 * 60)]
    [InlineData("15m", 15)]
    [InlineData("1w",  7 * 24 * 60)]
    [InlineData("4w",  4 * 7 * 24 * 60)]
    public void Parse_returns_window_for_format_driven_ranges(string range, int expectedWindowMinutes)
    {
        var (start, end) = AnalyticsRangeParser.Parse(range);

        start.Should().NotBeNull();
        end.Should().NotBeNull();
        (end!.Value - start!.Value).TotalMinutes.Should().BeApproximately(expectedWindowMinutes, 0.5);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("unknown")]
    [InlineData("5min")]      // "min" is not a single-char unit
    [InlineData("h")]          // missing magnitude
    [InlineData("0h")]         // non-positive magnitude
    [InlineData("-3h")]        // negative magnitude
    [InlineData("3y")]         // unsupported unit
    [InlineData("3.5h")]       // non-integer magnitude
    public void Parse_returns_null_pair_for_null_or_unparseable(string? range)
    {
        var (start, end) = AnalyticsRangeParser.Parse(range);
        start.Should().BeNull();
        end.Should().BeNull();
    }

    [Fact]
    public void Parse_is_case_insensitive()
    {
        var (start, end) = AnalyticsRangeParser.Parse("24H");
        start.Should().NotBeNull();
        end.Should().NotBeNull();
    }
}
