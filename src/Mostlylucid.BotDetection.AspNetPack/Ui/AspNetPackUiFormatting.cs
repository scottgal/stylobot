namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     Shared formatting helpers used by AspNetPack sub-row views. Keeps
///     the "X seconds ago" / "Y minutes" humanisation in one place so the
///     sub-rows and their header-strip tiles all read identically.
///     Static helpers, no dependencies -- safe to call from view components,
///     builders, and tests.
/// </summary>
public static class AspNetPackUiFormatting
{
    /// <summary>
    ///     Renders the elapsed time since <paramref name="lastSeen"/> relative to
    ///     <paramref name="now"/> as a compact "X ago" string. Null inputs render
    ///     as a hyphen so empty cells have a visible placeholder.
    /// </summary>
    public static string FormatTimeSince(DateTimeOffset? lastSeen, DateTimeOffset now)
    {
        if (lastSeen is null) return "-";
        var delta = now - lastSeen.Value;
        if (delta < TimeSpan.FromSeconds(2))  return "just now";
        if (delta < TimeSpan.FromMinutes(1))  return $"{(int)delta.TotalSeconds}s ago";
        if (delta < TimeSpan.FromHours(1))    return $"{(int)delta.TotalMinutes}m ago";
        if (delta < TimeSpan.FromDays(1))     return $"{(int)delta.TotalHours}h ago";
        return $"{(int)delta.TotalDays}d ago";
    }

    /// <summary>
    ///     Renders a <see cref="TimeSpan"/> in compact form ("0s", "30s", "5m",
    ///     "2h", "3d"). Used for "age" tiles where we already know the elapsed
    ///     interval and don't need the "ago" suffix.
    /// </summary>
    public static string FormatTimeSpan(TimeSpan value)
    {
        if (value < TimeSpan.FromSeconds(1))  return "0s";
        if (value < TimeSpan.FromMinutes(1))  return $"{(int)value.TotalSeconds}s";
        if (value < TimeSpan.FromHours(1))    return $"{(int)value.TotalMinutes}m";
        if (value < TimeSpan.FromDays(1))     return $"{(int)value.TotalHours}h";
        return $"{(int)value.TotalDays}d";
    }
}
