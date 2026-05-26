using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Sub-change 2: GetCountryStatsAsync — bytes_out aggregation, audience filter, time window.
/// </summary>
public class SqliteCountryStatsAggregateTests
{
    [Fact]
    public async Task GetCountryStatsAsync_returns_bytes_out_summed_per_country()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("country-bytes");

        await fx.Store.AddDetectionAsync(MakeDetection("US", isBot: false, bytes: 1000));
        await fx.Store.AddDetectionAsync(MakeDetection("US", isBot: true,  bytes: 2000));
        await fx.Store.AddDetectionAsync(MakeDetection("DE", isBot: false, bytes: 500));

        var rows = await fx.Store.GetCountryStatsAsync();

        var us = rows.Single(r => r.CountryCode == "US");
        us.TotalCount.Should().Be(2);
        us.BotCount.Should().Be(1);
        us.HumanCount.Should().Be(1);
        us.BytesOut.Should().Be(3000);

        var de = rows.Single(r => r.CountryCode == "DE");
        de.TotalCount.Should().Be(1);
        de.BytesOut.Should().Be(500);
    }

    [Fact]
    public async Task GetCountryStatsAsync_audience_humans_excludes_bot_rows()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("country-audience-humans");

        // Two countries, mix of bots and humans
        await fx.Store.AddDetectionAsync(MakeDetection("US", isBot: false, bytes: 100));
        await fx.Store.AddDetectionAsync(MakeDetection("US", isBot: false, bytes: 200));
        await fx.Store.AddDetectionAsync(MakeDetection("US", isBot: true,  bytes: 999));
        await fx.Store.AddDetectionAsync(MakeDetection("FR", isBot: false, bytes: 50));
        await fx.Store.AddDetectionAsync(MakeDetection("FR", isBot: true,  bytes: 888));

        var rows = await fx.Store.GetCountryStatsAsync(audienceFilter: "humans");

        // With filter=humans the bot rows are excluded entirely
        rows.Should().HaveCount(2, "both countries have human rows");

        var us = rows.Single(r => r.CountryCode == "US");
        us.TotalCount.Should().Be(2, "two human rows for US");
        us.BotCount.Should().Be(0);
        us.BytesOut.Should().Be(300);

        var fr = rows.Single(r => r.CountryCode == "FR");
        fr.TotalCount.Should().Be(1);
        fr.BytesOut.Should().Be(50);
    }

    [Fact]
    public async Task GetCountryStatsAsync_audience_bots_excludes_human_rows()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("country-audience-bots");

        await fx.Store.AddDetectionAsync(MakeDetection("US", isBot: false, bytes: 100));
        await fx.Store.AddDetectionAsync(MakeDetection("US", isBot: true,  bytes: 1000));
        await fx.Store.AddDetectionAsync(MakeDetection("US", isBot: true,  bytes: 2000));
        await fx.Store.AddDetectionAsync(MakeDetection("JP", isBot: true,  bytes: 500));
        await fx.Store.AddDetectionAsync(MakeDetection("JP", isBot: false, bytes: 999));

        var rows = await fx.Store.GetCountryStatsAsync(audienceFilter: "bots");

        var us = rows.Single(r => r.CountryCode == "US");
        us.TotalCount.Should().Be(2, "only bot rows counted");
        us.BotCount.Should().Be(2);
        us.HumanCount.Should().Be(0);
        us.BytesOut.Should().Be(3000);

        var jp = rows.Single(r => r.CountryCode == "JP");
        jp.TotalCount.Should().Be(1);
        jp.BytesOut.Should().Be(500);
    }

    [Fact]
    public async Task GetCountryStatsAsync_audience_filter_changes_row_count_not_just_arithmetic()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("country-audience-rowcount");

        // UK has only bot traffic; AU has only human traffic
        await fx.Store.AddDetectionAsync(MakeDetection("GB", isBot: true,  bytes: 100));
        await fx.Store.AddDetectionAsync(MakeDetection("GB", isBot: true,  bytes: 200));
        await fx.Store.AddDetectionAsync(MakeDetection("AU", isBot: false, bytes: 300));
        await fx.Store.AddDetectionAsync(MakeDetection("AU", isBot: false, bytes: 400));

        var humans = await fx.Store.GetCountryStatsAsync(audienceFilter: "humans");
        var bots   = await fx.Store.GetCountryStatsAsync(audienceFilter: "bots");
        var all    = await fx.Store.GetCountryStatsAsync();

        // humans: only AU appears (GB has no human rows)
        humans.Should().ContainSingle(r => r.CountryCode == "AU");
        humans.Should().NotContain(r => r.CountryCode == "GB");

        // bots: only GB appears (AU has no bot rows)
        bots.Should().ContainSingle(r => r.CountryCode == "GB");
        bots.Should().NotContain(r => r.CountryCode == "AU");

        // all: both appear
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetCountryStatsAsync_honours_startTime_endTime_window()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("country-time");

        var t0 = DateTime.UtcNow.AddHours(-3);
        var t1 = DateTime.UtcNow.AddHours(-1);

        await fx.Store.AddDetectionAsync(
            MakeDetection("US", isBot: false, bytes: 100) with { Timestamp = t0 });
        await fx.Store.AddDetectionAsync(
            MakeDetection("DE", isBot: false, bytes: 200) with { Timestamp = t1 });

        var rows = await fx.Store.GetCountryStatsAsync(
            startTime: DateTime.UtcNow.AddHours(-2),
            endTime:   DateTime.UtcNow);

        rows.Should().ContainSingle(r => r.CountryCode == "DE");
        rows.Should().NotContain(r => r.CountryCode == "US");
    }

    // --- helpers ---

    private static DashboardDetectionEvent MakeDetection(string country, bool isBot, long bytes) =>
        new()
        {
            RequestId        = Guid.NewGuid().ToString("N")[..12],
            Timestamp        = DateTime.UtcNow,
            IsBot            = isBot,
            BotProbability   = isBot ? 0.9 : 0.1,
            Confidence       = 0.5,
            RiskBand         = isBot ? "High" : "Low",
            Method           = "GET",
            Path             = "/",
            StatusCode       = 200,
            ProcessingTimeMs = 5,
            CountryCode      = country,
            ResponseBytes    = bytes,
        };
}
