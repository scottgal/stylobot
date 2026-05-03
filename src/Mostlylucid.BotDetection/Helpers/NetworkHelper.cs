using System.Net;
using System.Net.Sockets;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Shared helper for local/private IP address detection.
///     Consolidates the logic previously duplicated across IpContributor,
///     DetectionBroadcastMiddleware, SignatureFeedbackHandler, and GeoContributor.
/// </summary>
public static class NetworkHelper
{
    // Local/private IP prefixes for fast string-based checks
    private static readonly string[] LocalPrefixes =
    [
        "127.", "10.", "172.16.", "172.17.", "172.18.", "172.19.", "172.20.", "172.21.",
        "172.22.", "172.23.", "172.24.", "172.25.", "172.26.", "172.27.", "172.28.",
        "172.29.", "172.30.", "172.31.", "192.168.", "::1", "fe80:", "localhost"
    ];

    /// <summary>
    ///     Check if an IP address string represents a local/private network address.
    ///     Supports IPv4, IPv6, IPv4-mapped IPv6, and the "localhost" string.
    /// </summary>
    public static bool IsLocalIp(string? ip)
    {
        if (string.IsNullOrEmpty(ip))
            return true;

        // Fast path: string prefix check for common IPv4/IPv6 ranges
        foreach (var prefix in LocalPrefixes)
            if (ip.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;

        // Full IPAddress parsing for accurate IPv4/IPv6 checks
        if (IPAddress.TryParse(ip, out var addr))
            return IsLocalIp(addr);

        return false;
    }

    /// <summary>
    ///     Check if a parsed IPAddress is a local/private network address.
    ///     Supports IPv4, IPv6, link-local, site-local, ULA, and IPv4-mapped IPv6.
    /// </summary>
    public static bool IsLocalIp(IPAddress? ip)
    {
        if (ip == null) return false;
        if (IPAddress.IsLoopback(ip)) return true;

        if (ip.IsIPv6LinkLocal) return true;
        if (ip.IsIPv6SiteLocal) return true;

        // IPv6 unique local address (fc00::/7 - ULA, equivalent to RFC 1918)
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            var bytes = ip.GetAddressBytes();
            if ((bytes[0] & 0xFE) == 0xFC) return true;
        }

        // IPv4-mapped IPv6 (::ffff:10.x.x.x etc.)
        if (ip.IsIPv4MappedToIPv6)
            ip = ip.MapToIPv4();

        // IPv4 private ranges
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var bytes = ip.GetAddressBytes();
            return bytes[0] switch
            {
                10 => true,                                    // 10.0.0.0/8
                172 => bytes[1] >= 16 && bytes[1] <= 31,       // 172.16.0.0/12
                192 => bytes[1] == 168,                        // 192.168.0.0/16
                169 => bytes[1] == 254,                        // 169.254.0.0/16 (link-local)
                127 => true,                                   // 127.0.0.0/8
                _ => false
            };
        }

        return false;
    }
}
