using System.Net;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Shared helper for privacy-safe IP masking in logs and signals.
///     Consolidates the MaskIp logic previously duplicated across
///     IpContributor, IpDetector, ProjectHoneypotLookupService,
///     GeoContributor, and VerifiedBotRegistry.
/// </summary>
public static class PrivacyHelper
{
    /// <summary>
    ///     Mask an IP address for logging/display (zero-PII).
    ///     IPv4: shows first 3 octets, replaces last with "xxx".
    ///     IPv6: truncates to first 10 characters.
    /// </summary>
    public static string MaskIp(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length == 4)
            return $"{parts[0]}.{parts[1]}.{parts[2]}.xxx";

        // IPv6: truncate
        if (ip.Length > 10)
            return ip[..10] + "...";

        return ip;
    }

    /// <summary>
    ///     Mask a parsed IPAddress for logging/display (zero-PII).
    /// </summary>
    public static string MaskIp(IPAddress ip) => MaskIp(ip.ToString());
}
