using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Task 6: GetSummaryAsync — BytesOut, P95/Avg/Max processing time, time-window and audience filters.
/// </summary>
public class SqliteSummaryTests
{
    // ── helpers ────────────────────────────────────────────────────────────────

    private static DashboardDetectionEvent MakeDetection(
        bool isBot, double ms, long bytes, DateTime? timestamp = null) =>
        new()
        {
            RequestId        = Guid.NewGuid().ToString("N")[..12],
            Timestamp        = timestamp ?? DateTime.UtcNow,
            IsBot            = isBot,
            BotProbability   = isBot ? 0.9 : 0.1,
            Confidence       = 0.5,
            RiskBand         = isBot ? "High" : "Low",
            Method           = "GET",
            Path             = "/test",
            StatusCode       = 200,
            ProcessingTimeMs = ms,
            ResponseBytes    = bytes,
        };

    // ── tests ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetSummaryAsync_returns_bytes_out_p95_avg_and_max_processing_time()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("summary-kpi");

        // 19 × 10 ms + 1 × 100 ms; 20 × 1000 bytes = 20 000 bytes out.
        for (var i = 0; i < 19; i++)
            await fx.Store.AddDetectionAsync(MakeDetection(isBot: false, ms: 10, bytes: 1_000));
        await fx.Store.AddDetectionAsync(MakeDetection(isBot: false, ms: 100, bytes: 1_000));

        var summary = await fx.Store.GetSummaryAsync();

        summary.BytesOut.Should().Be(20_000);

        // avg = (19×10 + 100) / 20 = 290/20 = 14.5
        summary.AverageProcessingTimeMs.Should().BeApproximately(14.5, 0.1);
        summary.MaxProcessingTimeMs.Should().BeApproximately(100.0, 0.1);

        // p95 approx = avg + (max - avg) × 0.9 = 14.5 + 85.5×0.9 = 14.5 + 76.95 = 91.45
        summary.P95ProcessingTimeMs.Should().BeApproximately(91.45, 0.5);
    }

    [Fact]
    public async Task GetSummaryAsync_no_args_preserves_legacy_6h_window()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("summary-6h-window");

        var within  = MakeDetection(isBot: false, ms: 10, bytes: 100)
                          with { Timestamp = DateTime.UtcNow.AddHours(-3) };
        var outside = MakeDetection(isBot: false, ms: 10, bytes: 100)
                          with { Timestamp = DateTime.UtcNow.AddHours(-10) };

        await fx.Store.AddDetectionAsync(within);
        await fx.Store.AddDetectionAsync(outside);

        var summary = await fx.Store.GetSummaryAsync();

        summary.TotalRequests.Should().Be(1);
    }

    [Fact]
    public async Task GetSummaryAsync_respects_startTime_endTime_window()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("summary-window");

        var recent = MakeDetection(isBot: false, ms: 10, bytes: 100)
                         with { Timestamp = DateTime.UtcNow.AddHours(-1) };
        var old    = MakeDetection(isBot: false, ms: 10, bytes: 100)
                         with { Timestamp = DateTime.UtcNow.AddHours(-3) };

        await fx.Store.AddDetectionAsync(recent);
        await fx.Store.AddDetectionAsync(old);

        var summary = await fx.Store.GetSummaryAsync(
            startTime: DateTime.UtcNow.AddHours(-2),
            endTime:   DateTime.UtcNow);

        // Only the detection 1h ago falls inside the 2h-to-now window.
        summary.TotalRequests.Should().Be(1);
    }

    [Fact]
    public async Task GetSummaryAsync_audience_humans_filters_request_level_counts_and_bytes()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("summary-audience-humans");

        await fx.Store.AddDetectionAsync(MakeDetection(isBot: false, ms: 5, bytes: 200));
        for (var i = 0; i < 4; i++)
            await fx.Store.AddDetectionAsync(MakeDetection(isBot: true, ms: 5, bytes: 1_000));

        var summary = await fx.Store.GetSummaryAsync(audienceFilter: "humans");

        summary.TotalRequests.Should().Be(1);
        summary.BotRequests.Should().Be(0);
        summary.HumanRequests.Should().Be(1);
        summary.BytesOut.Should().Be(200);
    }

    [Fact]
    public async Task GetSummaryAsync_audience_bots_filters_to_bots_only()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("summary-audience-bots");

        await fx.Store.AddDetectionAsync(MakeDetection(isBot: false, ms: 5, bytes: 200));
        for (var i = 0; i < 4; i++)
            await fx.Store.AddDetectionAsync(MakeDetection(isBot: true, ms: 5, bytes: 1_000));

        var summary = await fx.Store.GetSummaryAsync(audienceFilter: "bots");

        summary.TotalRequests.Should().Be(4);
        summary.BotRequests.Should().Be(4);
        summary.HumanRequests.Should().Be(0);
        summary.BytesOut.Should().Be(4_000);
    }
}
