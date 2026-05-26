using FluentAssertions;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Sub-change 1: confirms the GetDetectionsAsync row-mapper populates ResponseBytes
///     from the detections table column added in Task 1/2.
/// </summary>
public class SqliteDetectionReaderResponseBytesTests
{
    [Fact]
    public async Task GetDetectionsAsync_returns_response_bytes_when_set()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("reader-bytes");

        var detection = MakeDetection(bytes: 12345);
        await fx.Store.AddDetectionAsync(detection);

        var rows = await fx.Store.GetDetectionsAsync();

        rows.Should().ContainSingle();
        rows[0].ResponseBytes.Should().Be(12345);
    }

    [Fact]
    public async Task GetDetectionsAsync_returns_null_response_bytes_when_not_set()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("reader-bytes-null");

        var detection = MakeDetection(bytes: null);
        await fx.Store.AddDetectionAsync(detection);

        var rows = await fx.Store.GetDetectionsAsync();

        rows.Should().ContainSingle();
        rows[0].ResponseBytes.Should().BeNull();
    }

    [Fact]
    public async Task GetDetectionsAsync_returns_correct_bytes_for_multiple_detections()
    {
        await using var fx = await SqliteDashboardStoreFixture.NewAsync("reader-bytes-multi");

        await fx.Store.AddDetectionAsync(MakeDetection(bytes: 100));
        await fx.Store.AddDetectionAsync(MakeDetection(bytes: 200));
        await fx.Store.AddDetectionAsync(MakeDetection(bytes: null));

        var rows = await fx.Store.GetDetectionsAsync();

        rows.Should().HaveCount(3);
        rows.Select(r => r.ResponseBytes)
            .Should().BeEquivalentTo(new long?[] { 200, 100, null },
                because: "rows come back newest-first and null maps to null");
    }

    // --- helpers ---

    private static DashboardDetectionEvent MakeDetection(long? bytes) =>
        new()
        {
            RequestId        = Guid.NewGuid().ToString("N")[..12],
            Timestamp        = DateTime.UtcNow,
            IsBot            = false,
            BotProbability   = 0.1,
            Confidence       = 0.5,
            RiskBand         = "Low",
            Method           = "GET",
            Path             = "/test",
            StatusCode       = 200,
            ProcessingTimeMs = 5,
            ResponseBytes    = bytes,
        };
}
