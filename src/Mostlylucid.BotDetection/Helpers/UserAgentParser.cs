namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Static facade around <see cref="UapCoreUserAgentParser"/>. Returns non-PII
///     family / OS / device-class / category strings; the canonical mappings live
///     in YAML (uap-core's regexes.yaml plus ua-categories.yaml) so adding a new
///     browser or tool family requires only editing data, never code.
/// </summary>
public static class UserAgentParser
{
    /// <summary>
    ///     Family + version from the UA. Both come from the same uap-core match
    ///     (same regex, same capture groups, expanded via the rule's v1/v2/v3
    ///     replacement fields). Returns ("Other", null) when no upstream rule
    ///     matches; callers display "Other" or pivot to a fallback.
    /// </summary>
    public static (string Family, string? Version) Parse(string ua)
        => UapCoreUserAgentParser.Default.Parse(ua) ?? ("Other", null);

    /// <summary>
    ///     OS family token for the visitor (e.g. "Windows", "macOS", "Linux",
    ///     "iOS", "Android"). Reads uap-core's os_parsers and normalises the
    ///     family for callers that expect "macOS" rather than uap-core's
    ///     "Mac OS X". Returns null when uap-core does not recognise the OS.
    /// </summary>
    public static string? ExtractOs(string ua)
        => UapCoreUserAgentParser.Default.Os(ua)?.Family;

    /// <summary>
    ///     Coarse "browser" / "tool" category for the residual when the family
    ///     is not a known bot. Reads ua-categories.yaml. Returns null when the
    ///     family is unknown to that file; the dashboard substitutes "unknown".
    /// </summary>
    public static string? ClassifyFamily(string? family)
        => UapCoreUserAgentParser.Default.Category(family);

    /// <summary>
    ///     Coarse device class ("Desktop" / "Mobile" / "Tablet") for the visitor.
    ///     Backed by uap-core's device_parsers; the class is derived from the
    ///     uap-core device family (iPad and "*Tablet*" → Tablet, "Other"
    ///     / "Desktop" / "Mac" → Desktop, everything else → Mobile). Returns
    ///     null when no device rule matches the UA at all (typical for raw
    ///     CLI tools, headless scrapers, anything that does not advertise a
    ///     device).
    /// </summary>
    public static string? ClassifyDeviceClass(string? ua)
        => ua is null ? null : UapCoreUserAgentParser.Default.DeviceClass(ua);
}