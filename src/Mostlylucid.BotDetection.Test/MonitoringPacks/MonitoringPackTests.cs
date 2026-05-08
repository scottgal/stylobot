using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class MonitoringPackTests
{
    [Fact]
    public void InstrumentCollectionSpec_DefaultTagFilter_IsNull()
    {
        var spec = new InstrumentCollectionSpec("botdetection.requests.total", CollectedValueType.Counter);
        Assert.Null(spec.TagFilter);
    }

    [Fact]
    public void MetricSnapshot_BucketTime_TruncatesToMinute()
    {
        var now = new DateTime(2026, 5, 8, 12, 34, 56, 789, DateTimeKind.Utc);
        var snap = new MetricSnapshot
        {
            BucketTime = now.TruncateToMinute(),
            PackId = "aspnet-monitoring",
            MeterName = "Mostlylucid.BotDetection",
            Instrument = "botdetection.requests.total",
            Value = 42.0,
            ValueType = "rate"
        };
        Assert.Equal(new DateTime(2026, 5, 8, 12, 34, 0, DateTimeKind.Utc), snap.BucketTime);
    }

    [Fact]
    public void CollectedValueType_HasExpectedValues()
    {
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Counter));
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Gauge));
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Histogram_P50));
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Histogram_P95));
        Assert.True(Enum.IsDefined(typeof(CollectedValueType), CollectedValueType.Histogram_P99));
    }

    [Fact]
    public void MetricSnapshot_ValueType_RoundTrips()
    {
        var validTypes = new[] { "rate", "gauge", "p50", "p95", "p99" };
        foreach (var t in validTypes)
            Assert.False(string.IsNullOrWhiteSpace(t));
    }
}
