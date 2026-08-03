using Mostlylucid.BotDetection.Console.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Console.Tests.Services;

public sealed class LiveDetectionTableFormattingTests
{
    [Theory]
    [InlineData(0.5, "500µs")]
    [InlineData(0.001, "1µs")]
    [InlineData(5.2, "5.2ms")]
    [InlineData(9.9, "9.9ms")]
    [InlineData(55, "55ms")]
    [InlineData(200, "200ms")]
    [InlineData(999, "999ms")]
    [InlineData(1500, "1.5s")]
    [InlineData(-1, "-")]
    [InlineData(double.NaN, "-")]
    public void FormatLatency_adapts_to_magnitude(double ms, string expected)
    {
        var result = LiveDetectionTableService.FormatLatency(ms);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(0, "now")]
    [InlineData(12, "12s")]
    [InlineData(90, "1m")]
    [InlineData(3600, "1h")]
    [InlineData(86400, "24h")]
    public void FormatAgo_adapts_to_scale(int seconds, string expected)
    {
        var result = LiveDetectionTableService.FormatAgo(TimeSpan.FromSeconds(seconds));
        Assert.Equal(expected, result);
    }

    [Fact]
    public void FormatAgo_now_for_zero_timespan()
    {
        Assert.Equal("now", LiveDetectionTableService.FormatAgo(TimeSpan.Zero));
    }

    [Fact]
    public void FormatAgo_now_for_negative_timespan()
    {
        Assert.Equal("now", LiveDetectionTableService.FormatAgo(TimeSpan.FromSeconds(-5)));
    }
}
