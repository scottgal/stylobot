using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins <see cref="SqliteDashboardEventStore.GetSignatureTimeSeriesAsync"/> -- the
///     signature-scoped read-through union (stream-'s ruling, 2026-08-21) that replaced
///     SignatureAggregateCache's ScoreHistory/ProcessingTimeHistory/ConfidenceHistory
///     shadow ring buffers for the signature-detail sparkline. Single source of truth:
///     the detections table, bucketed and gap-filled the same way GetTimeSeriesAsync
///     already is for the audience-scoped charts.
/// </summary>
public sealed class SqliteDashboardEventStoreSignatureTimeSeriesTests : IAsyncDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"stylobot-sigts-{Guid.NewGuid():N}");
    private readonly SqliteDashboardEventStore _store;

    public SqliteDashboardEventStoreSignatureTimeSeriesTests()
    {
        Directory.CreateDirectory(_tempDir);
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db")
        });
        _store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, options);
    }

    private static DashboardDetectionEvent MakeDetection(
        string signature, DateTime ts, double botProbability, double confidence, double processingTimeMs) => new()
    {
        RequestId = Guid.NewGuid().ToString("N"),
        Timestamp = ts,
        IsBot = botProbability >= 0.5,
        BotProbability = botProbability,
        Confidence = confidence,
        ProcessingTimeMs = processingTimeMs,
        PrimarySignature = signature,
        RiskBand = "High",
        Method = "GET",
        Path = "/",
    };

    [Fact]
    public async Task Averages_multiple_detections_in_the_same_bucket_and_gap_fills_quiet_buckets()
    {
        var end = DateTime.UtcNow;
        var start = end.AddMinutes(-5);

        // Two detections in the same (current) minute -- must average, not sum or overwrite.
        await _store.AddDetectionAsync(MakeDetection("sig-avg", end, botProbability: 0.8, confidence: 0.9, processingTimeMs: 10));
        await _store.AddDetectionAsync(MakeDetection("sig-avg", end, botProbability: 0.6, confidence: 0.7, processingTimeMs: 20));
        // One detection 3 minutes ago, in a different bucket.
        await _store.AddDetectionAsync(MakeDetection("sig-avg", end.AddMinutes(-3), botProbability: 0.4, confidence: 0.5, processingTimeMs: 30));

        var series = await _store.GetSignatureTimeSeriesAsync("sig-avg", start, end.AddSeconds(1), TimeSpan.FromMinutes(1));

        // 5 one-minute buckets covering [start, end].
        series.Should().HaveCount(6);

        var currentBucket = series[^1];
        currentBucket.TotalCount.Should().Be(2);
        currentBucket.AvgBotProbability.Should().BeApproximately(0.7, 0.001);
        currentBucket.AvgConfidence.Should().BeApproximately(0.8, 0.001);
        currentBucket.AvgProcessingTimeMs.Should().BeApproximately(15.0, 0.001);

        var threeMinAgoBucket = series[2];
        threeMinAgoBucket.TotalCount.Should().Be(1);
        threeMinAgoBucket.AvgBotProbability.Should().BeApproximately(0.4, 0.001);

        // A bucket with no activity is present and honestly zero, not missing.
        var quietBucket = series[0];
        quietBucket.TotalCount.Should().Be(0);
        quietBucket.AvgBotProbability.Should().Be(0.0);
    }

    [Fact]
    public async Task Different_signature_does_not_bleed_into_the_series()
    {
        var now = DateTime.UtcNow;
        await _store.AddDetectionAsync(MakeDetection("sig-a", now, 0.9, 0.9, 5));
        await _store.AddDetectionAsync(MakeDetection("sig-b", now, 0.1, 0.1, 5));

        var series = await _store.GetSignatureTimeSeriesAsync("sig-a", now.AddMinutes(-1), now.AddSeconds(1), TimeSpan.FromMinutes(1));

        series[^1].TotalCount.Should().Be(1);
        series[^1].AvgBotProbability.Should().BeApproximately(0.9, 0.001);
    }

    [Fact]
    public async Task Unknown_signature_returns_all_zero_gap_filled_series_not_an_error()
    {
        var now = DateTime.UtcNow;

        var series = await _store.GetSignatureTimeSeriesAsync("sig-never-seen", now.AddMinutes(-3), now, TimeSpan.FromMinutes(1));

        series.Should().NotBeEmpty();
        series.Should().OnlyContain(p => p.TotalCount == 0 && p.AvgBotProbability == 0.0);
    }

    public async ValueTask DisposeAsync()
    {
        await _store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true);
    }
}
