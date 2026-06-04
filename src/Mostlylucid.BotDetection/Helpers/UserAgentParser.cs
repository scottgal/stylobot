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
    ///     Extract browser family and version from a user agent string. Pure delegate to
    ///     <see cref="UapCoreUserAgentParser"/>, which reads uap-core's community-maintained
    ///     <c>regexes.yaml</c> (~4000 rules) -- both family AND version come from the SAME
    ///     match against the SAME regex, expanded via the rule's v1/v2/v3 replacement fields.
    ///     Returns "Other"/null when no upstream rule matches.
    ///
    ///     The previous body of this method was a hand-rolled switch over ~16 hard-coded
    ///     browser/tool tokens (Applebot returned "Safari" because the regex saw "Safari/"
    ///     before the "Applebot/" token; Mastodon, Iceshrimp, Pleroma, Misskey, Bing-Preview
    ///     all returned "Other"; Brave matched only on the literal "Brave" string which
    ///     modern Brave doesn't put in its UA). The switch is gone; the YAML carries the
    ///     canonical mapping and the update service refreshes it on a schedule.
    /// </summary>
    public static (string Family, string? Version) Parse(string ua)
    {
        var parsed = UapCoreUserAgentParser.Default.Parse(ua);
        return parsed ?? ("Other", null);
    }

    /// <summary>
    ///     Extract the OS family from a User-Agent string. Returns one of
    ///     "Windows", "macOS", "Linux", "iOS", "Android", or null when no recognised
    ///     OS token is present. Non-PII; intended for the display-name composer.
    /// </summary>
    public static string? ExtractOs(string ua)
    {
        if (string.IsNullOrEmpty(ua)) return null;
        if (ua.Contains("Windows", StringComparison.OrdinalIgnoreCase)) return "Windows";
        // iOS check before macOS — iPhone/iPad UAs contain "Mac OS X" too.
        if (ua.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("iPod", StringComparison.OrdinalIgnoreCase)) return "iOS";
        if (ua.Contains("Android", StringComparison.OrdinalIgnoreCase)) return "Android";
        if (ua.Contains("Mac OS X", StringComparison.OrdinalIgnoreCase)
            || ua.Contains("Macintosh", StringComparison.OrdinalIgnoreCase)) return "macOS";
        if (ua.Contains("Linux", StringComparison.OrdinalIgnoreCase)) return "Linux";
        return null;
    }

    /// <summary>
    ///     Classifies a family name returned by <see cref="Parse(string)"/> into one of
    ///     three coarse buckets: <c>"browser"</c>, <c>"tool"</c>, or <c>null</c> when the
    ///     family is unrecognised. Browser families cover the parser's known browser
    ///     outputs (Chrome, Firefox, Safari, Edge, Opera, Brave, Vivaldi, IE); tool
    ///     families cover the parser's known CLI / library outputs (curl, Python, Go,
    ///     Java, Node.js, wget). Bots and unknown families return <c>null</c> -- callers
    ///     wanting bot categorisation should consult <c>BotPatternLoader</c> first and
    ///     fall back to this method for the residual.
    ///
    ///     Case-insensitive. The single source of truth for which families this parser
    ///     emits; if <see cref="Parse(string)"/> learns a new family, add it here too.
    /// </summary>
    public static string? ClassifyFamily(string? family)
    {
        if (string.IsNullOrWhiteSpace(family)) return null;
        return family.Trim().ToLowerInvariant() switch
        {
            "chrome" or "firefox" or "safari" or "edge"
                or "opera" or "brave" or "vivaldi" or "internet explorer" => "browser",
            "curl" or "python" or "go" or "java" or "node.js" or "wget" => "tool",
            _ => null,
        };
    }

    /// <summary>
    ///     Derives a coarse device class (Desktop / Mobile / Tablet) from a raw
    ///     User-Agent string. Uses OS-level tokens (iPhone, Android, iPad) rather
    ///     than the parsed browser family so that iOS Chrome / iOS Safari are
    ///     correctly classified as Mobile instead of Desktop.
    ///
    ///     Returns null when the UA is absent, empty, or does not contain
    ///     recognisable browser tokens (bots, crawlers, CLI tools).
    ///
    ///     Accepts either a raw UA string or a short family name; if passed a
    ///     family name the function falls back to the family-based switch so
    ///     callers do not need to be updated.
    /// </summary>
    public static string? ClassifyDeviceClass(string? ua)
    {
        if (string.IsNullOrWhiteSpace(ua))
            return null;

        var u = ua.Trim();

        // Tablet check first -- iPad UA contains "Mobile" token on iOS 13+
        if (u.Contains("iPad", StringComparison.OrdinalIgnoreCase)
            || u.Contains("Tablet", StringComparison.OrdinalIgnoreCase))
            return "Tablet";

        // Mobile OS tokens take precedence over browser family
        if (u.Contains("iPhone", StringComparison.OrdinalIgnoreCase)
            || u.Contains("Android", StringComparison.OrdinalIgnoreCase)
            || u.Contains("Mobile", StringComparison.OrdinalIgnoreCase))
            return "Mobile";

        // Recognised desktop browser families (full UA string or family name)
        if (u.Contains("Chrome/", StringComparison.Ordinal)
            || u.Contains("Firefox/", StringComparison.Ordinal)
            || u.Contains("Safari/", StringComparison.Ordinal)
            || u.Contains("Edg/", StringComparison.Ordinal)
            || u.Contains("OPR/", StringComparison.Ordinal)
            || u.Contains("MSIE", StringComparison.Ordinal)
            || u.Contains("Trident/", StringComparison.Ordinal))
            return "Desktop";

        // Family-name fallback (for callers that pass the ua.family signal)
        return u switch
        {
            "Chrome" or "Firefox" or "Safari" or "Edge"
                or "Opera" or "Internet Explorer" or "Brave" or "Vivaldi" => "Desktop",
            _ => null,
        };
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
