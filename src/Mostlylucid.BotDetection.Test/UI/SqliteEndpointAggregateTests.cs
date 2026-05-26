using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

public class SqliteEndpointAggregateTests
{
    [Fact]
    public async Task GetEndpointStatsAsync_returns_bytes_out_and_human_count_alongside_bot_count()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("endpoint-bytes");

        // 1 human, 200 bytes, 5 ms
        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: false, bytes: 200, ms: 5));
        // 4 bots, 1000 bytes each, 20 ms each
        for (var i = 0; i < 4; i++)
            await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: true, bytes: 1000, ms: 20));

        var rows = await fx.Store.GetEndpointStatsAsync(audienceFilter: null);
        var widget = rows.Single(r => r.Path == "/api/widget");

        widget.TotalCount.Should().Be(5);
        widget.BotCount.Should().Be(4);
        widget.HumanCount.Should().Be(1);
        widget.BytesOut.Should().Be(4200);
    }

    [Fact]
    public async Task GetEndpointStatsAsync_audience_humans_filters_to_human_rows_only()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("endpoint-humans");

        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: false, bytes: 200, ms: 5));
        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: true,  bytes: 1000, ms: 20));

        var rows = await fx.Store.GetEndpointStatsAsync(audienceFilter: "humans");
        var widget = rows.Single(r => r.Path == "/api/widget");

        widget.TotalCount.Should().Be(1);
        widget.HumanCount.Should().Be(1);
        widget.BotCount.Should().Be(0);
        widget.BytesOut.Should().Be(200);
    }

    [Fact]
    public async Task GetEndpointStatsAsync_audience_bots_filters_to_bot_rows_only()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("endpoint-bots");

        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: false, bytes: 200, ms: 5));
        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: true,  bytes: 1000, ms: 20));

        var rows = await fx.Store.GetEndpointStatsAsync(audienceFilter: "bots");
        var widget = rows.Single(r => r.Path == "/api/widget");

        widget.TotalCount.Should().Be(1);
        widget.HumanCount.Should().Be(0);
        widget.BotCount.Should().Be(1);
        widget.BytesOut.Should().Be(1000);
    }

    [Fact]
    public async Task GetEndpointStatsAsync_honours_startTime_endTime_window()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("endpoint-time");

        var t0 = DateTime.UtcNow.AddHours(-3);
        var t1 = DateTime.UtcNow.AddHours(-1);

        var old    = MakeDetection("/api/old",    isBot: false, bytes: 100, ms: 5) with { Timestamp = t0 };
        var recent = MakeDetection("/api/recent", isBot: false, bytes: 200, ms: 5) with { Timestamp = t1 };
        await fx.Store.AddDetectionAsync(old);
        await fx.Store.AddDetectionAsync(recent);

        var rows = await fx.Store.GetEndpointStatsAsync(
            startTime: DateTime.UtcNow.AddHours(-2),
            endTime:   DateTime.UtcNow);

        rows.Select(r => r.Path).Should().BeEquivalentTo(new[] { "/api/recent" });
    }

    // --- detection helpers ---

    private static DashboardDetectionEvent MakeDetection(string path, bool isBot, long bytes, double ms) =>
        new()
        {
            RequestId        = Guid.NewGuid().ToString("N")[..12],
            Timestamp        = DateTime.UtcNow,
            IsBot            = isBot,
            BotProbability   = isBot ? 0.9 : 0.1,
            Confidence       = 0.5,
            RiskBand         = isBot ? "High" : "Low",
            Method           = "GET",
            Path             = path,
            StatusCode       = 200,
            ProcessingTimeMs = ms,
            ResponseBytes    = bytes,
        };
}
