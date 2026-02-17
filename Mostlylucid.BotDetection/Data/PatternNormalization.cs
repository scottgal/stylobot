using System.Security.Cryptography;
using System.Text;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Shared normalization for pattern reputation keys.
///     All producers and consumers of reputation patterns MUST use these methods
///     to ensure pattern IDs are consistent across the pipeline:
///     - FastPathReputationContributor (reads)
///     - ReputationBiasContributor (reads)
///     - ReputationMaintenanceService (writes)
///     - LlmClassificationCoordinator (writes)
/// </summary>
public static class PatternNormalization
{
    /// <summary>
    ///     Normalize User-Agent for pattern matching.
    ///     Extracts key indicators (browser, OS, bot signals, length bucket),
    ///     sorts alphabetically, and joins with commas.
    /// </summary>
    public static string NormalizeUserAgent(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua))
            return "empty";

        var lower = ua.ToLowerInvariant().Trim();
        var indicators = new List<string>(12);

        // Browser detection (mutually exclusive, order matters)
        if (lower.Contains("chrome")) indicators.Add("chrome");
        else if (lower.Contains("firefox")) indicators.Add("firefox");
        else if (lower.Contains("safari")) indicators.Add("safari");
        else if (lower.Contains("edge")) indicators.Add("edge");

        // OS detection (mutually exclusive)
        if (lower.Contains("windows")) indicators.Add("windows");
        else if (lower.Contains("mac")) indicators.Add("macos");
        else if (lower.Contains("linux")) indicators.Add("linux");
        else if (lower.Contains("android")) indicators.Add("android");
        else if (lower.Contains("iphone") || lower.Contains("ipad")) indicators.Add("ios");

        // Bot indicators (can be multiple)
        if (lower.Contains("bot")) indicators.Add("bot");
        if (lower.Contains("crawler")) indicators.Add("crawler");
        if (lower.Contains("spider")) indicators.Add("spider");
        if (lower.Contains("scraper")) indicators.Add("scraper");
        if (lower.Contains("headless")) indicators.Add("headless");
        if (lower.Contains("python")) indicators.Add("python");
        if (lower.Contains("curl")) indicators.Add("curl");
        if (lower.Contains("wget")) indicators.Add("wget");

        // Length bucket
        var lengthBucket = ua.Length switch
        {
            < 20 => "tiny",
            < 50 => "short",
            < 150 => "normal",
            < 300 => "long",
            _ => "huge"
        };
        indicators.Add($"len:{lengthBucket}");

        return string.Join(",", indicators.OrderBy(x => x));
    }

    /// <summary>
    ///     Normalize IP to CIDR range for pattern matching.
    ///     IPv4 → /24, IPv6 → /48.
    /// </summary>
    public static string NormalizeIpToRange(string ip)
    {
        if (string.IsNullOrWhiteSpace(ip))
            return "unknown";

        // Handle IPv6
        if (ip.Contains(':'))
        {
            var parts = ip.Split(':');
            if (parts.Length >= 3) return $"{parts[0]}:{parts[1]}:{parts[2]}::/48";
            return ip;
        }

        // Handle IPv4 - normalize to /24
        var octets = ip.Split('.');
        if (octets.Length == 4) return $"{octets[0]}.{octets[1]}.{octets[2]}.0/24";

        return ip;
    }

    /// <summary>
    ///     Create a pattern ID for a User-Agent string.
    ///     Returns "ua:{first16charsOfSHA256}".
    /// </summary>
    public static string CreateUaPatternId(string userAgent)
    {
        var normalized = NormalizeUserAgent(userAgent);
        var hash = ComputeHash(normalized);
        return $"ua:{hash}";
    }

    /// <summary>
    ///     Create a pattern ID for an IP address.
    ///     Returns "ip:{CIDR range}".
    /// </summary>
    public static string CreateIpPatternId(string ip)
    {
        var normalized = NormalizeIpToRange(ip);
        return $"ip:{normalized}";
    }

    /// <summary>
    ///     Compute SHA256 hash of input, return first 16 hex chars.
    /// </summary>
    public static string ComputeHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }
}
