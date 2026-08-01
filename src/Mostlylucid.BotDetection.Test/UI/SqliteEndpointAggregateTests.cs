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
    public async Task GetEndpointStatsAsync_marks_honeypot_paths_via_path_classifier()
    {
        // Pin the dashboard regression "we don't see ANY honeypot hits": the
        // IsHoneypot field was declared on DashboardEndpointStats and read by
        // the badge in SbEndpointsList, but NEVER populated by the store.
        // Every row read back with IsHoneypot=false so the badge never rendered.
        // The store now derives the flag per-row from HoneypotPathDefinitions.
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("endpoint-honeypot-flag");

        await fx.Store.AddDetectionAsync(MakeDetection("/wp-admin/admin.php", isBot: true,  bytes: 100, ms: 5));
        await fx.Store.AddDetectionAsync(MakeDetection("/index.html",         isBot: false, bytes: 200, ms: 5));

        var rows = await fx.Store.GetEndpointStatsAsync(audienceFilter: null);

        rows.Single(r => r.Path == "/wp-admin/admin.php").IsHoneypot.Should().BeTrue();
        rows.Single(r => r.Path == "/index.html").IsHoneypot.Should().BeFalse();
    }

    [Fact]
    public async Task GetEndpointStatsAsync_audience_honeypot_filters_to_honeypot_paths_only()
    {
        // Pin the operator-driven filter: ?audience=honeypot returns only rows
        // whose path classifies as honeypot. The other rows are dropped post-
        // query (cheap; the path classifier runs per row anyway).
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("endpoint-honeypot-filter");

        await fx.Store.AddDetectionAsync(MakeDetection("/.env",          isBot: true,  bytes: 80,  ms: 3));
        await fx.Store.AddDetectionAsync(MakeDetection("/.aws/credentials", isBot: true,  bytes: 60,  ms: 4));
        await fx.Store.AddDetectionAsync(MakeDetection("/index.html",    isBot: false, bytes: 300, ms: 6));
        await fx.Store.AddDetectionAsync(MakeDetection("/api/products",  isBot: false, bytes: 500, ms: 8));

        var rows = await fx.Store.GetEndpointStatsAsync(audienceFilter: "honeypot");

        rows.Select(r => r.Path).Should().BeEquivalentTo(new[] { "/.env", "/.aws/credentials" });
        rows.Should().OnlyContain(r => r.IsHoneypot);
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

    [Fact]
    public async Task GetEndpointStatsAsync_buckets_upstream_status_and_counts_no_origin_call_rows()
    {
        // mae-'s Endpoints UPSTREAM/RETURNED spec: a path can have a mix of
        // forwarded (real origin status) and blocked/honeypot/throttled (no
        // origin call at all) traffic, so the upstream axis needs the same
        // bucketed-count shape as Status2xx/3xx/4xx/5xx, plus an explicit
        // "no origin call" count rather than folding it into any bucket.
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("endpoint-upstream-buckets");

        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: false, bytes: 200, ms: 5) with { UpstreamStatusCode = 200 });
        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: false, bytes: 200, ms: 5) with { UpstreamStatusCode = 200 });
        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: true,  bytes: 100, ms: 3) with { UpstreamStatusCode = 500 });
        await fx.Store.AddDetectionAsync(MakeDetection("/api/widget", isBot: true,  bytes: 0,   ms: 1) with { StatusCode = 403, UpstreamStatusCode = null });

        var rows = await fx.Store.GetEndpointStatsAsync(audienceFilter: null);
        var widget = rows.Single(r => r.Path == "/api/widget");

        widget.UpstreamStatus2xx.Should().Be(2);
        widget.UpstreamStatus5xx.Should().Be(1);
        widget.UpstreamStatus3xx.Should().Be(0);
        widget.UpstreamStatus4xx.Should().Be(0);
        widget.UpstreamNoneCount.Should().Be(1);
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
