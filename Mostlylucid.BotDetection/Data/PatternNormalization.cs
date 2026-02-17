using System.IO.Hashing;
using System.Text;
using SysBitOps = System.Numerics.BitOperations;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Shared normalization for pattern reputation keys.
///     All producers and consumers of reputation patterns MUST use these methods
///     to ensure pattern IDs are consistent across the pipeline:
///     - FastPathReputationContributor (reads)
///     - ReputationBiasContributor (reads)
///     - ReputationMaintenanceService (writes)
///     - LlmClassificationCoordinator (writes)
///     Optimized for hot-path use (called per-request in Wave 0).
/// </summary>
public static class PatternNormalization
{
    // Indicator flags — bit positions are in ALPHABETICAL order so iterating
    // set bits low→high emits an already-sorted sequence. No LINQ OrderBy needed.
    [Flags]
    private enum Indicator : uint
    {
        Android   = 1 << 0,
        Bot       = 1 << 1,
        Chrome    = 1 << 2,
        Crawler   = 1 << 3,
        Curl      = 1 << 4,
        Edge      = 1 << 5,
        Firefox   = 1 << 6,
        Headless  = 1 << 7,
        Ios       = 1 << 8,
        LenHuge   = 1 << 9,
        LenLong   = 1 << 10,
        LenNormal = 1 << 11,
        LenShort  = 1 << 12,
        LenTiny   = 1 << 13,
        Linux     = 1 << 14,
        Macos     = 1 << 15,
        Python    = 1 << 16,
        Safari    = 1 << 17,
        Scraper   = 1 << 18,
        Spider    = 1 << 19,
        Wget      = 1 << 20,
        Windows   = 1 << 21
    }

    // Lookup: bit position → indicator string (alphabetical order matches bit order)
    private static readonly string[] IndicatorStrings =
    [
        "android",    // 0
        "bot",        // 1
        "chrome",     // 2
        "crawler",    // 3
        "curl",       // 4
        "edge",       // 5
        "firefox",    // 6
        "headless",   // 7
        "ios",        // 8
        "len:huge",   // 9
        "len:long",   // 10
        "len:normal", // 11
        "len:short",  // 12
        "len:tiny",   // 13
        "linux",      // 14
        "macos",      // 15
        "python",     // 16
        "safari",     // 17
        "scraper",    // 18
        "spider",     // 19
        "wget",       // 20
        "windows"     // 21
    ];

    /// <summary>
    ///     Normalize User-Agent for pattern matching.
    ///     Extracts key indicators (browser, OS, bot signals, length bucket),
    ///     outputs them in alphabetical order joined with commas.
    ///     Uses OrdinalIgnoreCase to avoid ToLowerInvariant() allocation,
    ///     and bitmask approach to avoid List + LINQ OrderBy allocations.
    /// </summary>
    public static string NormalizeUserAgent(string ua)
    {
        if (string.IsNullOrWhiteSpace(ua))
            return "empty";

        var flags = (Indicator)0;

        // Browser detection (mutually exclusive, order matters)
        if (ua.Contains("chrome", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Chrome;
        else if (ua.Contains("firefox", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Firefox;
        else if (ua.Contains("safari", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Safari;
        else if (ua.Contains("edge", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Edge;

        // OS detection (mutually exclusive)
        if (ua.Contains("windows", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Windows;
        else if (ua.Contains("mac", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Macos;
        else if (ua.Contains("linux", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Linux;
        else if (ua.Contains("android", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Android;
        else if (ua.Contains("iphone", StringComparison.OrdinalIgnoreCase) ||
                 ua.Contains("ipad", StringComparison.OrdinalIgnoreCase))
            flags |= Indicator.Ios;

        // Bot indicators (can be multiple)
        if (ua.Contains("bot", StringComparison.OrdinalIgnoreCase)) flags |= Indicator.Bot;
        if (ua.Contains("crawler", StringComparison.OrdinalIgnoreCase)) flags |= Indicator.Crawler;
        if (ua.Contains("spider", StringComparison.OrdinalIgnoreCase)) flags |= Indicator.Spider;
        if (ua.Contains("scraper", StringComparison.OrdinalIgnoreCase)) flags |= Indicator.Scraper;
        if (ua.Contains("headless", StringComparison.OrdinalIgnoreCase)) flags |= Indicator.Headless;
        if (ua.Contains("python", StringComparison.OrdinalIgnoreCase)) flags |= Indicator.Python;
        if (ua.Contains("curl", StringComparison.OrdinalIgnoreCase)) flags |= Indicator.Curl;
        if (ua.Contains("wget", StringComparison.OrdinalIgnoreCase)) flags |= Indicator.Wget;

        // Length bucket
        flags |= ua.Length switch
        {
            < 20 => Indicator.LenTiny,
            < 50 => Indicator.LenShort,
            < 150 => Indicator.LenNormal,
            < 300 => Indicator.LenLong,
            _ => Indicator.LenHuge
        };

        return BuildSortedString(flags);
    }

    /// <summary>
    ///     Build comma-separated sorted indicator string from bitmask.
    ///     Bits are in alphabetical order, so iterating low→high emits sorted output.
    /// </summary>
    private static string BuildSortedString(Indicator flags)
    {
        var sb = new StringBuilder(48);
        var bits = (uint)flags;

        while (bits != 0)
        {
            var pos = SysBitOps.TrailingZeroCount(bits);
            if (sb.Length > 0) sb.Append(',');
            sb.Append(IndicatorStrings[pos]);
            bits &= bits - 1; // clear lowest set bit
        }

        return sb.ToString();
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

        // Handle IPv4 - normalize to /24 without Split allocation
        var first = ip.IndexOf('.');
        if (first < 0) return ip;
        var second = ip.IndexOf('.', first + 1);
        if (second < 0) return ip;
        var third = ip.IndexOf('.', second + 1);
        if (third < 0) return ip;

        return string.Concat(ip.AsSpan(0, third), ".0/24");
    }

    /// <summary>
    ///     Create a pattern ID for a User-Agent string.
    ///     Returns "ua:{16charXxHash64}".
    /// </summary>
    public static string CreateUaPatternId(string userAgent)
    {
        var normalized = NormalizeUserAgent(userAgent);
        return string.Concat("ua:", ComputeHash(normalized));
    }

    /// <summary>
    ///     Create a pattern ID for an IP address.
    ///     Returns "ip:{CIDR range}".
    /// </summary>
    public static string CreateIpPatternId(string ip)
    {
        var normalized = NormalizeIpToRange(ip);
        return string.Concat("ip:", normalized);
    }

    /// <summary>
    ///     Compute non-cryptographic hash for pattern ID generation.
    ///     Uses XxHash64 (~10x faster than SHA256) — we need collision resistance
    ///     for cache keys, not cryptographic strength.
    ///     Returns 16 lowercase hex chars (full 64-bit hash).
    /// </summary>
    public static string ComputeHash(string input)
    {
        var byteCount = Encoding.UTF8.GetByteCount(input);
        Span<byte> inputBytes = byteCount <= 512
            ? stackalloc byte[byteCount]
            : new byte[byteCount];
        Encoding.UTF8.GetBytes(input, inputBytes);

        var hash = XxHash64.HashToUInt64(inputBytes);
        return hash.ToString("x16");
    }
}
