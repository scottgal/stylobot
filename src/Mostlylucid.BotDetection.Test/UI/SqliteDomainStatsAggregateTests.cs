using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     GetDomainStatsAsync — raw per-host rows (Domain, Requests, Bots, IsInternal) for ALL
///     observed domains: bot count on the shared BotFloor (so it reconciles with the summary),
///     internal self-traffic flagged but NOT excluded (the licensed-vs-pool split is the
///     commercial overlay's job), ordered by requests descending.
/// </summary>
public class SqliteDomainStatsAggregateTests
{
    [Fact]
    public async Task GetDomainStatsAsync_returns_raw_per_host_counts_ordered_by_requests_desc()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("domain-raw");

        // a.com: 3 rows (1 bot); b.com: 1 human row
        await fx.Store.AddDetectionAsync(MakeDetection("a.com", isBot: true));
        await fx.Store.AddDetectionAsync(MakeDetection("a.com", isBot: false));
        await fx.Store.AddDetectionAsync(MakeDetection("a.com", isBot: false));
        await fx.Store.AddDetectionAsync(MakeDetection("b.com", isBot: false));

        var rows = await fx.Store.GetDomainStatsAsync();

        rows.Should().HaveCount(2);
        rows[0].Domain.Should().Be("a.com", "ordered by requests desc");
        rows[0].Requests.Should().Be(3);
        rows[0].Bots.Should().Be(1, "one row is over the BotFloor");
        rows[0].IsInternal.Should().BeFalse();

        var b = rows.Single(r => r.Domain == "b.com");
        b.Requests.Should().Be(1);
        b.Bots.Should().Be(0);
    }

    [Fact]
    public async Task GetDomainStatsAsync_flags_pure_internal_host_but_does_not_exclude_it()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("domain-internal");

        // localhost: all Internal self-traffic; shop.com: real customer traffic (+ one stray internal row)
        await fx.Store.AddDetectionAsync(MakeDetection("localhost", isBot: false, botType: "Internal"));
        await fx.Store.AddDetectionAsync(MakeDetection("localhost", isBot: false, botType: "Internal"));
        await fx.Store.AddDetectionAsync(MakeDetection("shop.com", isBot: false));
        await fx.Store.AddDetectionAsync(MakeDetection("shop.com", isBot: false, botType: "Internal"));

        var rows = await fx.Store.GetDomainStatsAsync();

        // ALL observed domains returned — internal is flagged, never excluded (commercial overlay decides)
        rows.Should().HaveCount(2);

        rows.Single(r => r.Domain == "localhost").IsInternal
            .Should().BeTrue("every row is Internal self-traffic");
        rows.Single(r => r.Domain == "shop.com").IsInternal
            .Should().BeFalse("it has non-Internal rows, so it is a real domain");
    }

    [Fact]
    public async Task GetDomainStatsAsync_honours_time_window()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("domain-window");

        await fx.Store.AddDetectionAsync(
            MakeDetection("old.com", isBot: false) with { Timestamp = DateTime.UtcNow.AddHours(-3) });
        await fx.Store.AddDetectionAsync(
            MakeDetection("new.com", isBot: false) with { Timestamp = DateTime.UtcNow.AddHours(-1) });

        var rows = await fx.Store.GetDomainStatsAsync(
            startTime: DateTime.UtcNow.AddHours(-2), endTime: DateTime.UtcNow);

        rows.Should().ContainSingle(r => r.Domain == "new.com");
        rows.Should().NotContain(r => r.Domain == "old.com");
    }

    // --- helpers ---

    private static DashboardDetectionEvent MakeDetection(string domain, bool isBot, string? botType = null) =>
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
            CountryCode      = "US",
            ResponseBytes    = 0,
            Domain           = domain,
            BotType          = botType,
        };
}
