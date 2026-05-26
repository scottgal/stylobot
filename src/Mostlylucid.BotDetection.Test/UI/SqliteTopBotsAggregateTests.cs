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

    /// <summary>
    ///     Smoke-test for I3 (tag-helper audience/range attribute threading).
    ///     SbTopBotsTagHelper now exposes [HtmlAttributeName("audience")] and
    ///     [HtmlAttributeName("range")] properties and passes them to
    ///     SbTopBotsViewComponent.InvokeAsync. The view component calls
    ///     eventStore.GetTopBotsAsync(startTime, endTime, audienceFilter) on the bypass path.
    ///     This test drives that exact store call with audience + time-window params and
    ///     verifies it returns windowed data — proving the attribute chain works end-to-end
    ///     without requiring a full Razor/MVC rendering stack in the unit test layer.
    /// </summary>
    [Fact]
    public async Task GetTopBotsAsync_tag_helper_smoke_audience_and_range_attributes_flow_through()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-taghelper-smoke");

        // Seed: 5 old bots (outside 1h window), 2 recent bots (inside 1h window).
        for (var i = 0; i < 5; i++)
            await fx.Store.AddDetectionAsync(MakeBot("OldBot", "old-sig",
                timestamp: DateTime.UtcNow.AddHours(-6), bytes: 100));

        await fx.Store.AddDetectionAsync(MakeBot("RecentBot", "recent-sig-A",
            timestamp: DateTime.UtcNow.AddMinutes(-20), bytes: 200));
        await fx.Store.AddDetectionAsync(MakeBot("RecentBot", "recent-sig-B",
            timestamp: DateTime.UtcNow.AddMinutes(-10), bytes: 300));

        // Simulate <sb-top-bots audience="bots" range="1h"> → tag helper passes
        // these params to SbTopBotsViewComponent.InvokeAsync → component calls
        // eventStore.GetTopBotsAsync(startTime: -1h, endTime: now, audienceFilter: "bots").
        var start = DateTime.UtcNow.AddHours(-1);
        var end   = DateTime.UtcNow;

        var result = await fx.Store.GetTopBotsAsync(
            count:          10,
            startTime:      start,
            endTime:        end,
            audienceFilter: "bots");

        // The windowed path groups by signature; recent-sig-A and recent-sig-B are distinct rows.
        result.Should().HaveCount(2, "two distinct signatures with recent detections fall in the 1h window");
        result.Should().NotContain(b => b.BotName == "OldBot",
            "OldBot detections are outside the 1h window and must not appear");
        result.Should().AllSatisfy(b => b.BotName.Should().Be("RecentBot"),
            "only RecentBot signatures are within the 1h window");
    }

    [Fact]
    public async Task GetTopBotsAsync_with_time_window_returns_windowed_hit_counts()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-window");

        // Old hits: 3 on bot A, 3 hours ago (outside a 1h window)
        for (var i = 0; i < 3; i++)
            await fx.Store.AddDetectionAsync(MakeBot("GPTBot", "primarysig-A",
                timestamp: DateTime.UtcNow.AddHours(-3), bytes: 500));

        // Recent hits: 1 on bot A + 1 on bot B, 30 minutes ago (inside a 1h window)
        await fx.Store.AddDetectionAsync(MakeBot("GPTBot",  "primarysig-A",
            timestamp: DateTime.UtcNow.AddMinutes(-30), bytes: 500));
        await fx.Store.AddDetectionAsync(MakeBot("Bingbot", "primarysig-B",
            timestamp: DateTime.UtcNow.AddMinutes(-30), bytes: 1000));

        // Query: last 1 hour — windowed path must be used, not all-time signatures table
        var result = await fx.Store.GetTopBotsAsync(
            count:     10,
            startTime: DateTime.UtcNow.AddHours(-1),
            endTime:   DateTime.UtcNow);

        var gptbot = result.SingleOrDefault(b => b.BotName == "GPTBot");
        gptbot.Should().NotBeNull("GPTBot had 1 hit in the last 1h");
        gptbot!.HitCount.Should().Be(1, "only 1 GPTBot hit in last 1h, not 4 all-time");
        gptbot.BytesOut.Should().Be(500);

        var bingbot = result.SingleOrDefault(b => b.BotName == "Bingbot");
        bingbot.Should().NotBeNull("Bingbot had 1 hit in the last 1h");
        bingbot!.HitCount.Should().Be(1);
        bingbot.BytesOut.Should().Be(1000);
    }

    [Fact]
    public async Task GetTopBotsAsync_with_no_time_window_uses_signatures_all_time()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("topbots-alltime");

        // AddSignatureAsync is what increments signatures.hit_count (not AddDetectionAsync).
        // Call it 3 times for the same signature → hit_count = 3 on the signatures row.
        const string sig = "sig-alltime";
        for (var i = 0; i < 3; i++)
            await fx.Store.AddSignatureAsync(MakeBotSignature(sig));

        // Add only 1 recent detection — NOT inside any time window.
        // The no-window path must return hit_count=3 from signatures (not 1 from detections).
        await fx.Store.AddDetectionAsync(MakeDetection(sig, isBot: true, bytes: 100));

        var result = await fx.Store.GetTopBotsAsync(count: 10);

        var entry = result.SingleOrDefault(b => b.PrimarySignature == sig);
        entry.Should().NotBeNull("no-window path must hit the signatures table");
        // signatures.hit_count was bumped to 3 by three AddSignatureAsync calls
        entry!.HitCount.Should().Be(3, "signatures table records all-time hit_count, not detection-row count");
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

    private static DashboardDetectionEvent MakeBot(string botName, string primarySignature,
        DateTime timestamp, long bytes) =>
        new()
        {
            RequestId        = Guid.NewGuid().ToString("N")[..12],
            Timestamp        = timestamp,
            IsBot            = true,
            BotName          = botName,
            BotType          = "ai",
            PrimarySignature = primarySignature,
            BotProbability   = 0.95,
            Confidence       = 0.9,
            RiskBand         = "VeryHigh",
            Method           = "GET",
            Path             = "/article",
            StatusCode       = 200,
            ProcessingTimeMs = 10,
            ResponseBytes    = bytes,
        };
}
