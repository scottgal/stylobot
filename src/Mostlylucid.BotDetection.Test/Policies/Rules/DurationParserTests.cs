using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Test.Policies.Rules;

/// <summary>
///     Coverage for <see cref="DurationParser"/>. Wave 5 promoted the parser
///     from internal to public so the dashboard's <c>PolicyStackFilter</c>
///     could stop hand-rolling its own <c>@since:</c> parser. These tests pin
///     the YAML-loader contract (single-suffix tokens), the dashboard contract
///     (hour/day window tokens), and the composite-suffix shape (1h30m) that
///     the merged parser had to learn.
/// </summary>
public class DurationParserTests
{
    // YAML-loader grammar (unchanged).
    [Theory]
    [InlineData("30s", 30)]
    [InlineData("5m", 5 * 60)]
    [InlineData("1h", 3600)]
    [InlineData("2d", 2 * 24 * 3600)]
    [InlineData("1.5m", 90)]
    public void Single_suffix_tokens_parse(string raw, double expectedSeconds)
    {
        Assert.True(DurationParser.TryParse(raw, out var ts));
        Assert.Equal(expectedSeconds, ts.TotalSeconds, precision: 6);
    }

    // Dashboard "since" tokens.
    [Theory]
    [InlineData("24h", 24)]
    [InlineData("7d", 7 * 24)]
    [InlineData("30d", 30 * 24)]
    public void Dashboard_since_tokens_parse_to_positive_window(string raw, double expectedHours)
    {
        Assert.True(DurationParser.TryParse(raw, out var ts));
        Assert.Equal(expectedHours, ts.TotalHours, precision: 6);
        Assert.True(ts > TimeSpan.Zero);
    }

    // Composite suffix groups. Wave 5 addition: needed so the dashboard's
    // hand-rolled compound parser could be retired without losing
    // expressive range.
    [Theory]
    [InlineData("1h30m", 90 * 60)]
    [InlineData("2m30s", 150)]
    [InlineData("1d12h", (24 + 12) * 3600)]
    public void Composite_suffix_groups_sum_each_segment(string raw, double expectedSeconds)
    {
        Assert.True(DurationParser.TryParse(raw, out var ts));
        Assert.Equal(expectedSeconds, ts.TotalSeconds, precision: 6);
    }

    [Fact]
    public void Composite_suffix_with_whitespace_between_groups_parses()
    {
        Assert.True(DurationParser.TryParse("1h 30m", out var ts));
        Assert.Equal(90 * 60, ts.TotalSeconds, precision: 6);
    }

    [Fact]
    public void Standard_TimeSpan_shape_falls_through_to_TimeSpan_TryParse()
    {
        Assert.True(DurationParser.TryParse("00:01:30", out var ts));
        Assert.Equal(90, ts.TotalSeconds, precision: 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("garbage")]
    [InlineData("5x")]
    [InlineData("h")]
    public void Garbage_input_returns_false_and_zero(string? raw)
    {
        Assert.False(DurationParser.TryParse(raw, out var ts));
        Assert.Equal(TimeSpan.Zero, ts);
    }

    [Fact]
    public void Case_insensitive_unit_suffix()
    {
        Assert.True(DurationParser.TryParse("30S", out var ts));
        Assert.Equal(30, ts.TotalSeconds, precision: 6);
    }
}
