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
    ///     Format a processing-time value (in milliseconds) for the dashboard.
    ///     StyloBot detection runs in microseconds-to-low-milliseconds: rendering
    ///     "0ms" for everything under 1ms is a lie ("we don't even SHOW how long
    ///     it took"). This formatter chooses the unit so a 320μs detector reads
    ///     as "320µs" instead of "0ms", a 1.4ms reads as "1.4ms", a 24ms reads
    ///     as "24ms", and a 1.4s reads as "1.4s". Anything that truly captured
    ///     zero (no timing instrument fired) reads as "-" so the operator can
    ///     tell "fast" from "not measured".
    /// </summary>
    public static string FormatMs(double ms)
    {
        if (ms <= 0) return "-";
        // Sub-microsecond is below our timing instrument's floor; the value
        // exists but the unit-resolution doesn't carry meaning -- mark it
        // as "fast" rather than rendering a misleading "0".
        if (ms < 0.001)  return "<1µs";
        if (ms < 1)      return $"{ms * 1000:F0}µs";
        if (ms < 10)     return $"{ms:F2}ms";
        if (ms < 1000)   return $"{ms:F0}ms";
        return $"{ms / 1000:F1}s";
    }
}