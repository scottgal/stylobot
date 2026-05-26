using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Task 6: GetTimeSeriesAsync — BytesOut, P95/Avg/Max per bucket, audience filter, gap-fill zeros.
/// </summary>
public class SqliteTimeSeriesTests
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
    public async Task GetTimeSeriesAsync_returns_bytes_out_per_bucket()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("timeseries-bytes");

        // 3 detections × 500 bytes = 1500 bytes in the current hour.
        for (var i = 0; i < 3; i++)
            await fx.Store.AddDetectionAsync(MakeDetection(isBot: false, ms: 10, bytes: 500));

        var series = await fx.Store.GetTimeSeriesAsync(
            startTime:  DateTime.UtcNow.AddHours(-1),
            endTime:    DateTime.UtcNow,
            bucketSize: TimeSpan.FromHours(1));

        // At least one bucket must contain the fresh detections.
        var populated = series.Where(p => p.TotalCount > 0).ToList();
        populated.Should().NotBeEmpty("expected a bucket with the 3 fresh detections");
        populated.Sum(p => p.BytesOut).Should().Be(1_500);
    }

    [Fact]
    public async Task GetTimeSeriesAsync_returns_p95_per_bucket()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("timeseries-p95");

        // 19 × 10 ms + 1 × 100 ms — same distribution as the summary test.
        for (var i = 0; i < 19; i++)
            await fx.Store.AddDetectionAsync(MakeDetection(isBot: false, ms: 10, bytes: 100));
        await fx.Store.AddDetectionAsync(MakeDetection(isBot: false, ms: 100, bytes: 100));

        // Wide bucket (4 h) so all 20 detections fall in one bucket.
        var series = await fx.Store.GetTimeSeriesAsync(
            startTime:  DateTime.UtcNow.AddHours(-1),
            endTime:    DateTime.UtcNow,
            bucketSize: TimeSpan.FromHours(4));

        var populated = series.Where(p => p.TotalCount > 0).ToList();
        populated.Should().NotBeEmpty();

        // avg = 14.5, p95 = 14.5 + (100 - 14.5) × 0.9 = 91.45
        populated[0].AvgProcessingTimeMs.Should().BeApproximately(14.5, 0.1);
        populated[0].P95ProcessingTimeMs.Should().BeApproximately(91.45, 0.5);
    }

    [Fact]
    public async Task GetTimeSeriesAsync_audience_humans_filters_bucket_counts_and_bytes()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("timeseries-audience-humans");

        // 5 humans × 100 bytes; 5 bots × 1000 bytes — all in the current minute.
        for (var i = 0; i < 5; i++)
            await fx.Store.AddDetectionAsync(MakeDetection(isBot: false, ms: 5, bytes: 100));
        for (var i = 0; i < 5; i++)
            await fx.Store.AddDetectionAsync(MakeDetection(isBot: true,  ms: 5, bytes: 1_000));

        var series = await fx.Store.GetTimeSeriesAsync(
            startTime:     DateTime.UtcNow.AddHours(-1),
            endTime:       DateTime.UtcNow,
            bucketSize:    TimeSpan.FromHours(1),
            audienceFilter: "humans");

        var populated = series.Where(p => p.TotalCount > 0).ToList();
        populated.Should().NotBeEmpty();

        populated.Sum(p => p.TotalCount).Should().Be(5);
        populated.Sum(p => p.HumanCount).Should().Be(5);
        populated.Sum(p => p.BotCount).Should().Be(0);
        populated.Sum(p => p.BytesOut).Should().Be(500);
    }

    [Fact]
    public async Task GetTimeSeriesAsync_gap_fill_emits_zero_for_new_fields_in_empty_buckets()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("timeseries-gapfill");

        // One detection 30 minutes ago — should appear in the last 1-hour bucket.
        await fx.Store.AddDetectionAsync(
            MakeDetection(isBot: false, ms: 10, bytes: 100)
                with { Timestamp = DateTime.UtcNow.AddMinutes(-30) });

        var series = await fx.Store.GetTimeSeriesAsync(
            startTime:  DateTime.UtcNow.AddHours(-4),
            endTime:    DateTime.UtcNow,
            bucketSize: TimeSpan.FromHours(1));

        // Should have 4 hourly buckets; the older three must be gap-filled with zeros.
        series.Should().HaveCountGreaterThanOrEqualTo(4);

        var emptyBuckets = series.Where(p => p.TotalCount == 0).ToList();
        emptyBuckets.Should().NotBeEmpty("gap-fill must produce empty buckets for hours with no data");

        foreach (var bucket in emptyBuckets)
        {
            bucket.BytesOut.Should().Be(0L);
            bucket.AvgProcessingTimeMs.Should().Be(0.0);
            bucket.P95ProcessingTimeMs.Should().Be(0.0);
            bucket.MaxProcessingTimeMs.Should().Be(0.0);
        }
    }
}
