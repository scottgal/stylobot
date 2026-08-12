using Mostlylucid.BotDetection.UI.Dashboard;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     <see cref="DashboardRoutingHelpers.BuildPinnedWindow"/> is the SINGLE derivation for
///     "what a default view of a page means", shared between the tick materializer's Tier 1
///     pinned prewarm and page controllers that read the composed envelope. The site page's
///     summary-0 P0 (2026-08-12) was this exact class of drift: the controller computed
///     bucket minutes as windowMinutes/60 (24h → 24) while the materializer used
///     <see cref="HitsPerPeriodChartletBuilder.BucketSizeForWindow"/> (24h → 20), so every
///     request keyed a different content-cache envelope than the prewarm — a permanent cold
///     miss and a permanently zero summary strip. These values lock the canonical shape.
/// </summary>
public sealed class DashboardPinnedWindowTests
{
    private static readonly DateTime FixedNow = new(2026, 8, 12, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("6h", 360, 5)]
    [InlineData("24h", 1440, 20)]
    [InlineData("7d", 10080, 120)]
    [InlineData("30d", 43200, 480)]
    public void BuildPinnedWindow_uses_the_chartlet_bucket_size(string token, int expectedMinutes, int expectedBucketMinutes)
    {
        var window = DashboardRoutingHelpers.BuildPinnedWindow(token, FixedNow);

        Assert.Equal(expectedMinutes, (window.EndTime!.Value - window.StartTime!.Value).TotalMinutes);
        Assert.Equal(expectedBucketMinutes, window.BucketMinutes);
        Assert.Equal("all", window.AudienceFilter);
        Assert.Null(window.ProbMin);
        Assert.Null(window.Domains);
        Assert.Equal(500, window.TopN);
    }

    [Fact]
    public void BuildPinnedWindow_unknown_token_falls_back_to_24h_shape()
    {
        var window = DashboardRoutingHelpers.BuildPinnedWindow("bogus", FixedNow);

        Assert.Equal(1440, (window.EndTime!.Value - window.StartTime!.Value).TotalMinutes);
        Assert.Equal(20, window.BucketMinutes);
    }

    [Fact]
    public void BuildPinnedWindow_passes_domains_through()
    {
        string[] domains = ["alpha.test", "beta.test"];

        var window = DashboardRoutingHelpers.BuildPinnedWindow("24h", FixedNow, domains);

        Assert.Equal(domains, window.Domains);
    }
}
