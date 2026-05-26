namespace Mostlylucid.BotDetection.UI.Helpers;

/// <summary>
///     Parses analytics-tab range strings ("1h", "6h", "24h", "7d", "30d") into a
///     concrete (startTime, endTime) pair anchored at the current UTC time.
///     Returns (null, null) for null/empty/unknown input so the caller can fall
///     through to the legacy default (cache snapshot / hardcoded 6h window).
/// </summary>
public static class AnalyticsRangeParser
{
    public static (DateTime? StartTime, DateTime? EndTime) Parse(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
            return (null, null);

        var now = DateTime.UtcNow;
        var window = range.Trim().ToLowerInvariant() switch
        {
            "1h"  => TimeSpan.FromHours(1),
            "6h"  => TimeSpan.FromHours(6),
            "24h" => TimeSpan.FromHours(24),
            "7d"  => TimeSpan.FromDays(7),
            "30d" => TimeSpan.FromDays(30),
            _     => (TimeSpan?)null,
        };
        if (window is null) return (null, null);
        return (now - window.Value, now);
    }
}