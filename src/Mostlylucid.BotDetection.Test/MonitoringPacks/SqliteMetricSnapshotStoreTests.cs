using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class SqliteMetricSnapshotStoreTests : IAsyncDisposable
{
    private readonly SqliteConnection _conn;
    private readonly SqliteMetricSnapshotStore _store;

    public SqliteMetricSnapshotStoreTests()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        _store = new SqliteMetricSnapshotStore(_conn, NullLogger<SqliteMetricSnapshotStore>.Instance);
        _store.InitializeAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task WriteAndRead_RoundTrips()
    {
        var bucket = DateTime.UtcNow.TruncateToMinute();
        var snap = new MetricSnapshot
        {
            BucketTime = bucket,
            PackId = "aspnet-monitoring",
            MeterName = "Mostlylucid.BotDetection",
            Instrument = "botdetection.requests.total",
            Value = 12.5,
            ValueType = "rate"
        };

        await _store.WriteSnapshotsAsync([snap]);
        var results = await _store.GetTimeSeriesAsync(
            "aspnet-monitoring", "botdetection.requests.total",
            bucket.AddMinutes(-1), bucket.AddMinutes(1));

        Assert.Single(results);
        Assert.Equal(12.5, results[0].Value);
        Assert.Equal("rate", results[0].ValueType);
    }

    [Fact]
    public async Task Prune_RemovesOldRows()
    {
        var old = DateTime.UtcNow.AddDays(-10).TruncateToMinute();
        await _store.WriteSnapshotsAsync([new MetricSnapshot
        {
            BucketTime = old, PackId = "aspnet-monitoring",
            MeterName = "x", Instrument = "y", Value = 1, ValueType = "rate"
        }]);

        var pruned = await _store.PruneOldSnapshotsAsync(DateTime.UtcNow.AddDays(-7));
        Assert.Equal(1, pruned);
    }

    [Fact]
    public async Task GetLatestSnapshots_ReturnsDistinctInstruments()
    {
        var bucket = DateTime.UtcNow.TruncateToMinute();
        await _store.WriteSnapshotsAsync([
            new MetricSnapshot { BucketTime = bucket, PackId = "aspnet-monitoring", MeterName = "x", Instrument = "requests", Value = 5, ValueType = "rate" },
            new MetricSnapshot { BucketTime = bucket, PackId = "aspnet-monitoring", MeterName = "x", Instrument = "latency", Value = 2.3, ValueType = "p50" }
        ]);

        var latest = await _store.GetLatestSnapshotsAsync("aspnet-monitoring");
        Assert.Equal(2, latest.Count);
    }

    public async ValueTask DisposeAsync()
    {
        await _conn.DisposeAsync();
    }
}
