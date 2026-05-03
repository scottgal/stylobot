using System.Net;

namespace Mostlylucid.BotDetection.Helpers;

/// <summary>
///     Utility class for CIDR subnet matching operations
/// </summary>
public static class CidrHelper
{
    /// <summary>
    ///     Checks if an IP address is within a CIDR subnet range
    /// </summary>
    /// <param name="ipAddress">The IP address to check</param>
    /// <param name="cidr">The CIDR notation (e.g., "10.0.0.0/8")</param>
    /// <returns>True if the IP is in the subnet, false otherwise</returns>
    public static bool IsInSubnet(IPAddress ipAddress, string cidr)
    {
        if (ipAddress == null || string.IsNullOrWhiteSpace(cidr))
            return false;

        var parts = cidr.Split('/');
        if (parts.Length != 2)
            return false;

        if (!IPAddress.TryParse(parts[0], out var networkAddress))
            return false;

        if (!int.TryParse(parts[1], out var prefixLength))
            return false;

        return IsInSubnet(ipAddress, networkAddress, prefixLength);
    }

    /// <summary>
    ///     Checks if an IP address is within a subnet range
    /// </summary>
    /// <param name="ipAddress">The IP address to check</param>
    /// <param name="networkAddress">The network address</param>
    /// <param name="prefixLength">The prefix length (subnet mask bits)</param>
    /// <returns>True if the IP is in the subnet, false otherwise</returns>
    public static bool IsInSubnet(IPAddress ipAddress, IPAddress networkAddress, int prefixLength)
    {
        if (ipAddress == null || networkAddress == null)
            return false;

        var ipBytes = ipAddress.GetAddressBytes();
        var networkBytes = networkAddress.GetAddressBytes();

        // IPv4 and IPv6 must match
        if (ipBytes.Length != networkBytes.Length)
            return false;

        // Validate prefix length
        var maxPrefixLength = ipBytes.Length * 8;
        if (prefixLength < 0 || prefixLength > maxPrefixLength)
            return false;

        var fullBytes = prefixLength / 8;
        var remainingBits = prefixLength % 8;

        // Check full bytes
        for (var i = 0; i < fullBytes; i++)
            if (ipBytes[i] != networkBytes[i])
                return false;

        // Check remaining bits with proper masking
        if (remainingBits > 0 && fullBytes < ipBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((ipBytes[fullBytes] & mask) != (networkBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    /// <summary>
    ///     Tries to parse a string IP address and check if it's in a CIDR range
    /// </summary>
    /// <param name="ipAddressString">The IP address string</param>
    /// <param name="cidr">The CIDR notation</param>
    /// <returns>True if the IP is in the subnet, false otherwise</returns>
    public static bool IsInSubnet(string? ipAddressString, string cidr)
    {
        if (string.IsNullOrWhiteSpace(ipAddressString))
            return false;

        if (!IPAddress.TryParse(ipAddressString, out var ipAddress))
            return false;

        return IsInSubnet(ipAddress, cidr);
    }
}