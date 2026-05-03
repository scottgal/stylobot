namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Shared helper for extracting browser family and version from User-Agent strings.
///     Consolidates the logic previously duplicated across UserAgentContributor,
///     DashboardSummaryBroadcaster, and DetectionBroadcastMiddleware.
///     Returns non-PII family names (e.g., "Chrome", "Firefox") safe for signals/dashboard.
/// </summary>
public static class UserAgentParser
{
    /// <summary>
    ///     Extract browser family and version from a user agent string.
    ///     Returns a tuple of (Family, Version) where Family is always non-null
    ///     and Version may be null for UAs without parseable versions.
    /// </summary>
    public static (string Family, string? Version) Parse(string ua)
    {
        // Order matters: check specific browsers before generic Chrome/Safari
        if (ua.Contains("Edg/", StringComparison.Ordinal))
            return ("Edge", ExtractVersion(ua, "Edg/"));
        if (ua.Contains("OPR/", StringComparison.Ordinal))
            return ("Opera", ExtractVersion(ua, "OPR/"));
        if (ua.Contains("Vivaldi/", StringComparison.Ordinal))
            return ("Vivaldi", ExtractVersion(ua, "Vivaldi/"));
        if (ua.Contains("Brave", StringComparison.Ordinal) && ua.Contains("Chrome/", StringComparison.Ordinal))
            return ("Brave", ExtractVersion(ua, "Chrome/"));
        if (ua.Contains("Firefox/", StringComparison.Ordinal))
            return ("Firefox", ExtractVersion(ua, "Firefox/"));
        if (ua.Contains("Chrome/", StringComparison.Ordinal))
            return ("Chrome", ExtractVersion(ua, "Chrome/"));
        if (ua.Contains("Safari/", StringComparison.Ordinal) && ua.Contains("Version/", StringComparison.Ordinal))
            return ("Safari", ExtractVersion(ua, "Version/"));
        if (ua.Contains("MSIE", StringComparison.Ordinal) || ua.Contains("Trident/", StringComparison.Ordinal))
            return ("Internet Explorer", null);
        // Bot/tool UAs
        if (ua.Contains("curl/", StringComparison.OrdinalIgnoreCase))
            return ("curl", ExtractVersion(ua, "curl/"));
        if (ua.Contains("python", StringComparison.OrdinalIgnoreCase))
            return ("Python", null);
        if (ua.Contains("Go-http-client", StringComparison.Ordinal))
            return ("Go", null);
        if (ua.Contains("Java/", StringComparison.Ordinal))
            return ("Java", ExtractVersion(ua, "Java/"));
        if (ua.Contains("node", StringComparison.OrdinalIgnoreCase))
            return ("Node.js", null);
        if (ua.Contains("wget", StringComparison.OrdinalIgnoreCase))
            return ("wget", null);
        // Known bots
        if (ua.Contains("Googlebot", StringComparison.OrdinalIgnoreCase))
            return ("Googlebot", null);
        if (ua.Contains("bingbot", StringComparison.OrdinalIgnoreCase))
            return ("Bingbot", null);
        if (ua.Contains("GPTBot", StringComparison.OrdinalIgnoreCase))
            return ("GPTBot", null);
        if (ua.Contains("ClaudeBot", StringComparison.OrdinalIgnoreCase))
            return ("ClaudeBot", null);

        return ("Other", null);
    }

    private static string? ExtractVersion(string ua, string token)
    {
        var idx = ua.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + token.Length;
        var end = start;
        while (end < ua.Length && ua[end] != ' ' && ua[end] != ';' && ua[end] != ')') end++;
        var ver = ua[start..end];
        // Return just major.minor for brevity
        var dotCount = 0;
        for (var i = 0; i < ver.Length; i++)
        {
            if (ver[i] == '.') dotCount++;
            if (dotCount == 2) return ver[..i];
        }
        return ver;
    }
}
