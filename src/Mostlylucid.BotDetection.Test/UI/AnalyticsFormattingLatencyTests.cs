using Mostlylucid.BotDetection.UI.Helpers;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins <see cref="AnalyticsFormatting.FormatMs"/> -- the dashboard's
///     cross-cutting latency formatter. StyloBot detection runs in
///     microseconds-to-low-milliseconds; the previous "ms:F0" format
///     rendered every sub-millisecond value as "0ms", which lied about
///     how much time the pipeline actually took. The unit-bucketed
///     formatter must show µs / ms / s with enough resolution to
///     differentiate a 320µs detector from a 24ms one without lying about
///     either.
/// </summary>
public class AnalyticsFormattingLatencyTests
{
    [Theory]
    [InlineData(0,        "-")]
    [InlineData(-1,       "-")]
    [InlineData(0.0005,   "<1µs")] // below the timing instrument's floor
    [InlineData(0.001,    "1µs")]
    [InlineData(0.320,    "320µs")]
    [InlineData(0.999,    "999µs")]
    [InlineData(1.0,      "1.00ms")]
    [InlineData(1.4,      "1.40ms")]
    [InlineData(9.99,     "9.99ms")]
    [InlineData(10.0,     "10ms")]
    [InlineData(24.0,     "24ms")]
    [InlineData(420.0,    "420ms")]
    [InlineData(999.0,    "999ms")]
    [InlineData(1000.0,   "1.0s")]
    [InlineData(1400.0,   "1.4s")]
    public void FormatMs_buckets_units_so_sub_ms_values_are_visible(double ms, string expected)
    {
        Assert.Equal(expected, AnalyticsFormatting.FormatMs(ms));
    }
}
