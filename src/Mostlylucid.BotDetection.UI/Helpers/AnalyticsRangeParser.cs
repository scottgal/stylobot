using System.Globalization;

namespace Mostlylucid.BotDetection.UI.Helpers;

/// <summary>
///     Parses analytics-tab range strings into a concrete (startTime, endTime)
///     pair anchored at the current UTC time. Accepts any "Nh" (hours), "Nd"
///     (days), "Nm" (minutes), or "Nw" (weeks) format -- N is any positive
///     integer. Returns (null, null) for null / empty / unparseable input so
///     the caller can fall through to its legacy default.
///
///     Format-driven rather than a hardcoded list so the analytics-tab options
///     (AnalyticsTabOptions.Picker.QuickRanges) can be operator-extended with
///     "2h", "12h", "14d", etc. without this parser becoming a parallel source
///     of truth that silently rots.
/// </summary>
public static class AnalyticsRangeParser
{
    public static (DateTime? StartTime, DateTime? EndTime) Parse(string? range)
    {
        if (string.IsNullOrWhiteSpace(range))
            return (null, null);

        var trimmed = range.Trim();
        if (trimmed.Length < 2)
            return (null, null);

        var unit = char.ToLowerInvariant(trimmed[^1]);
        var magnitudePart = trimmed[..^1];
        if (!int.TryParse(magnitudePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var magnitude) || magnitude <= 0)
            return (null, null);

        TimeSpan? window = unit switch
        {
            'm' => TimeSpan.FromMinutes(magnitude),
            'h' => TimeSpan.FromHours(magnitude),
            'd' => TimeSpan.FromDays(magnitude),
            'w' => TimeSpan.FromDays(magnitude * 7),
            _   => null,
        };
        if (window is null) return (null, null);

        var now = DateTime.UtcNow;
        return (now - window.Value, now);
    }
}
