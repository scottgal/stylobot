namespace Mostlylucid.BotDetection.UI.Helpers;

/// <summary>
///     Shared formatters for the analytics-tab widgets (bytes-out, latency).
///     Used by the FOSS widget templates so the convention is consistent
///     across the dashboard. Add new formatters here rather than duplicating
///     across templates.
/// </summary>
public static class AnalyticsFormatting
{
    /// <summary>
    ///     Format a byte count as a human-readable string. 0 renders as "-"
    ///     so columns don't visually shout zero for unwindowed / chunked rows.
    /// </summary>
    public static string FormatBytes(long bytes) => bytes switch
    {
        0                     => "-",
        < 1024                => $"{bytes} B",
        < 1024 * 1024         => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _                     => $"{bytes / (1024.0 * 1024 * 1024):F2} GB"
    };

    /// <summary>
    ///     Format a millisecond value as integer milliseconds. 0 renders as "-".
    /// </summary>
    public static string FormatMs(double ms) => ms > 0 ? $"{ms:F0}ms" : "-";
}