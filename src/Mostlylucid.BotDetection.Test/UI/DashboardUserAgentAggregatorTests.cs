using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Tests for <see cref="DashboardUserAgentAggregator" />: audience filtering,
///     time-range windowing, and bytes accumulation.
/// </summary>
public class DashboardUserAgentAggregatorTests
{
    // ─── audience="humans" ─────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeAsync_audience_humans_returns_only_human_rows()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-agg-humans");

        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0", isBot: false, bytes: 1000));
        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0", isBot: false, bytes: 500));
        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88",    isBot: true,  bytes: 9999));

        var aggregator = new DashboardUserAgentAggregator(fx.Store);
        var results = await aggregator.ComputeAsync(audienceFilter: "humans");

        // All returned rows must have zero bot detections
        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.BotCount.Should().Be(0));
        results.Any(r => r.Family.Equals("curl", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("curl is bot-only, should not appear for humans filter");
    }

    [Fact]
    public async Task ComputeAsync_audience_humans_bytes_accumulate_correctly()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-agg-humans-bytes");

        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0", isBot: false, bytes: 1000));
        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0", isBot: false, bytes: 2000));
        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88",    isBot: true,  bytes: 9999));

        var aggregator = new DashboardUserAgentAggregator(fx.Store);
        var results = await aggregator.ComputeAsync(audienceFilter: "humans");

        var chrome = results.FirstOrDefault(r => r.Family.Equals("Chrome", StringComparison.OrdinalIgnoreCase));
        chrome.Should().NotBeNull();
        chrome!.BytesOut.Should().Be(3000);
    }

    // ─── audience="bots" ───────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeAsync_audience_bots_returns_only_bot_rows()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-agg-bots");

        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0", isBot: false, bytes: 1000));
        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88",    isBot: true,  bytes: 500));
        await fx.Store.AddDetectionAsync(MakeDetection("Python/3.12",  isBot: true,  bytes: 200));

        var aggregator = new DashboardUserAgentAggregator(fx.Store);
        var results = await aggregator.ComputeAsync(audienceFilter: "bots");

        results.Should().NotBeEmpty();
        results.Should().AllSatisfy(r => r.HumanCount.Should().Be(0));
        results.Any(r => r.Family.Equals("Chrome", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("Chrome is human-only, should not appear for bots filter");
    }

    // ─── time-range windowing ──────────────────────────────────────────────────

    [Fact]
    public async Task ComputeAsync_startTime_endTime_restricts_to_window()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-agg-range");

        var old = DateTime.UtcNow.AddHours(-10);
        var recent = DateTime.UtcNow.AddMinutes(-5);

        // Old detection: outside the window
        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88", isBot: true, bytes: 9999) with { Timestamp = old });
        // Recent detection: inside the window
        await fx.Store.AddDetectionAsync(MakeDetection("Mozilla/5.0 (X11; Linux x86_64; rv:120.0) Gecko/20100101 Firefox/120.0", isBot: false, bytes: 300) with { Timestamp = recent });

        var windowStart = DateTime.UtcNow.AddHours(-1);
        var windowEnd   = DateTime.UtcNow.AddMinutes(1); // slightly in future for clock skew

        var aggregator = new DashboardUserAgentAggregator(fx.Store);
        var results = await aggregator.ComputeAsync(startTime: windowStart, endTime: windowEnd);

        results.Any(r => r.Family.Equals("curl", StringComparison.OrdinalIgnoreCase))
            .Should().BeFalse("curl detection is outside the time window");
        results.Any(r => r.Family.Equals("Firefox", StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("Firefox detection is inside the time window");
    }

    // ─── bytes accumulation ────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeAsync_bytes_out_summed_per_family_no_filter()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-agg-bytes-all");

        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0", isBot: false, bytes: 1000));
        await fx.Store.AddDetectionAsync(MakeDetection("Chrome/118.0", isBot: false, bytes: 2000));
        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88",    isBot: true,  bytes: 500));

        var aggregator = new DashboardUserAgentAggregator(fx.Store);
        var results = await aggregator.ComputeAsync(); // no filters

        var chrome = results.FirstOrDefault(r => r.Family.Equals("Chrome", StringComparison.OrdinalIgnoreCase));
        chrome.Should().NotBeNull();
        chrome!.BytesOut.Should().Be(3000);

        var curl = results.FirstOrDefault(r => r.Family.Equals("curl", StringComparison.OrdinalIgnoreCase));
        curl.Should().NotBeNull();
        curl!.BytesOut.Should().Be(500);
    }

    // ─── null bytes ────────────────────────────────────────────────────────────

    [Fact]
    public async Task ComputeAsync_null_bytes_treated_as_zero()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("ua-agg-null-bytes");

        await fx.Store.AddDetectionAsync(MakeDetection("curl/7.88", isBot: true, bytes: null));

        var aggregator = new DashboardUserAgentAggregator(fx.Store);
        var results = await aggregator.ComputeAsync();

        var curl = results.FirstOrDefault(r => r.Family.Equals("curl", StringComparison.OrdinalIgnoreCase));
        curl.Should().NotBeNull();
        curl!.BytesOut.Should().Be(0);
    }

    // ─── InferUaCategory returns canonical lowercase values ────────────────

    [Theory]
    [InlineData("Chrome",           "browser")]
    [InlineData("Firefox",          "browser")]
    [InlineData("Safari",           "browser")]
    [InlineData("Edge",             "browser")]
    [InlineData("Googlebot",        "search")]
    [InlineData("Bingbot",          "search")]
    [InlineData("DuckDuckBot",      "search")]
    [InlineData("GPTBot",           "ai")]
    [InlineData("ClaudeBot",        "ai")]
    [InlineData("CCBot",            "ai")]
    [InlineData("curl",             "tool")]
    [InlineData("python",           "tool")]
    [InlineData("wget",             "tool")]
    [InlineData("CompletelyUnknown","unknown")]
    public void InferUaCategory_returns_lowercase_canonical_value(string family, string expectedCategory)
    {
        var result = DashboardUserAgentAggregator.InferUaCategory(family);
        result.Should().Be(expectedCategory,
            $"InferUaCategory(\"{family}\") must return lowercase \"{expectedCategory}\" " +
            $"to match SbUserAgentsListViewComponent filter switch");
    }

    // ─── helpers ──────────────────────────────────────────────────────────────

    private static DashboardDetectionEvent MakeDetection(string userAgent, bool isBot, long? bytes) =>
        new()
        {
            RequestId        = Guid.NewGuid().ToString("N")[..12],
            Timestamp        = DateTime.UtcNow,
            IsBot            = isBot,
            BotProbability   = isBot ? 0.9 : 0.1,
            Confidence       = 0.5,
            RiskBand         = isBot ? "High" : "Low",
            Method           = "GET",
            Path             = "/page",
            StatusCode       = 200,
            ProcessingTimeMs = 5,
            UserAgentRaw     = userAgent,
            ResponseBytes    = bytes,
        };
}
