namespace Mostlylucid.BotDetection.MonitoringPacks;

public sealed class MetricSnapshot
{
    public long Id { get; set; }
    public DateTime BucketTime { get; set; }
    public required string PackId { get; set; }
    public required string MeterName { get; set; }
    public required string Instrument { get; set; }
    public string? Tags { get; set; }
    public double Value { get; set; }
    public required string ValueType { get; set; }
}

public static class DateTimeExtensions
{
    public static DateTime TruncateToMinute(this DateTime dt)
        => new(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, 0, dt.Kind);
}
