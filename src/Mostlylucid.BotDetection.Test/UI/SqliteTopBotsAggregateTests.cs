using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Sub-change 3: GetTopBotsAsync — bytes_out via correlated subquery, audience filter.
/// </summary>
public class SqliteTopBotsAggregateTests
{
    [Fact]
    public async Task GetTopBotsAsync_bytes_out_sums_detections_for_signature()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-bytes");

        const string sig = "bot-sig-alpha";
        await fx.Store.AddSignatureAsync(MakeBotSignature(sig));

        // 5 detections with 10000 bytes each = 50000 total
        for (var i = 0; i < 5; i++)
            await fx.Store.AddDetectionAsync(MakeDetection(sig, isBot: true, bytes: 10_000));

        var rows = await fx.Store.GetTopBotsAsync();

        rows.Should().ContainSingle(r => r.PrimarySignature == sig);
        rows[0].BytesOut.Should().Be(50_000);
    }

    [Fact]
    public async Task GetTopBotsAsync_bytes_out_is_zero_when_no_response_bytes_recorded()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-bytes-zero");

        const string sig = "bot-no-bytes";
        await fx.Store.AddSignatureAsync(MakeBotSignature(sig));
        await fx.Store.AddDetectionAsync(MakeDetection(sig, isBot: true, bytes: null));

        var rows = await fx.Store.GetTopBotsAsync();

        rows.Should().ContainSingle(r => r.PrimarySignature == sig);
        rows[0].BytesOut.Should().Be(0);
    }

    [Fact]
    public async Task GetTopBotsAsync_audience_humans_returns_empty_list()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-audience-humans");

        const string sig = "bot-sig-human-filter";
        await fx.Store.AddSignatureAsync(MakeBotSignature(sig));
        await fx.Store.AddDetectionAsync(MakeDetection(sig, isBot: true, bytes: 5000));

        var rows = await fx.Store.GetTopBotsAsync(audienceFilter: "humans");

        // top-bots is bots-only; "humans" audience returns nothing
        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetTopBotsAsync_audience_bots_returns_all_bot_entries()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-audience-bots");

        await fx.Store.AddSignatureAsync(MakeBotSignature("bot-a"));
        await fx.Store.AddSignatureAsync(MakeBotSignature("bot-b"));
        await fx.Store.AddDetectionAsync(MakeDetection("bot-a", isBot: true, bytes: 100));
        await fx.Store.AddDetectionAsync(MakeDetection("bot-b", isBot: true, bytes: 200));

        var rows = await fx.Store.GetTopBotsAsync(audienceFilter: "bots");

        rows.Should().HaveCount(2);
        rows.Should().Contain(r => r.PrimarySignature == "bot-a");
        rows.Should().Contain(r => r.PrimarySignature == "bot-b");
    }

    [Fact]
    public async Task GetTopBotsAsync_null_audience_returns_all_bot_entries()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-audience-null");

        await fx.Store.AddSignatureAsync(MakeBotSignature("bot-x"));
        await fx.Store.AddDetectionAsync(MakeDetection("bot-x", isBot: true, bytes: 300));

        var rows = await fx.Store.GetTopBotsAsync(audienceFilter: null);

        rows.Should().ContainSingle(r => r.PrimarySignature == "bot-x");
    }

    [Fact]
    public async Task GetTopBotsAsync_bytes_aggregated_independently_per_signature()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-per-sig");

        // Two signatures with different byte totals; query result must keep them separate.
        await fx.Store.AddSignatureAsync(MakeBotSignature("sig-heavy"));
        await fx.Store.AddSignatureAsync(MakeBotSignature("sig-light"));

        for (var i = 0; i < 3; i++)
            await fx.Store.AddDetectionAsync(MakeDetection("sig-heavy", isBot: true, bytes: 5_000));
        await fx.Store.AddDetectionAsync(MakeDetection("sig-light", isBot: true, bytes: 100));

        var rows = await fx.Store.GetTopBotsAsync();

        var heavy = rows.Single(r => r.PrimarySignature == "sig-heavy");
        var light = rows.Single(r => r.PrimarySignature == "sig-light");

        heavy.BytesOut.Should().Be(15_000);
        light.BytesOut.Should().Be(100);
    }

    // --- helpers ---

    private static DashboardSignatureEvent MakeBotSignature(string sig) =>
        new()
        {
            SignatureId      = sig,
            PrimarySignature = sig,
            Timestamp        = DateTime.UtcNow,
            RiskBand         = "High",
            IsKnownBot       = true,
            BotProbability   = 0.95,
            Confidence       = 0.9,
        };

    private static DashboardDetectionEvent MakeDetection(string sig, bool isBot, long? bytes) =>
        new()
        {
            RequestId        = Guid.NewGuid().ToString("N")[..12],
            Timestamp        = DateTime.UtcNow,
            PrimarySignature = sig,
            IsBot            = isBot,
            BotProbability   = isBot ? 0.95 : 0.1,
            Confidence       = 0.9,
            RiskBand         = isBot ? "High" : "Low",
            Method           = "GET",
            Path             = "/scrape",
            StatusCode       = 200,
            ProcessingTimeMs = 10,
            ResponseBytes    = bytes,
        };
}
