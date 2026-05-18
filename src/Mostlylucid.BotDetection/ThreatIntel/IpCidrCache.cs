using System.Net;

namespace Mostlylucid.BotDetection.ThreatIntel;

/// <summary>
///     In-memory IPv4 + IPv6 CIDR lookup. Stores prefixes as sorted (network, mask)
///     pairs per family + does a linear scan on read - fine for the ~600-entry feeds
///     we ship (Spamhaus DROP/EDROP, Tor exit list). Swap to a radix tree if a feed
///     grows past ~10k entries.
/// </summary>
internal sealed class IpCidrCache
{
    private readonly (uint Network, uint Mask)[] _v4;
    private readonly (UInt128 Network, UInt128 Mask)[] _v6;

    public IpCidrCache(IEnumerable<string> cidrs)
    {
        var v4 = new List<(uint, uint)>();
        var v6 = new List<(UInt128, UInt128)>();
        foreach (var raw in cidrs)
        {
            var cidr = raw.Trim();
            if (cidr.Length == 0) continue;
            if (!IPNetwork.TryParse(cidr, out var net)) continue;

            if (net.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                var (network, mask) = ToV4(net.BaseAddress, net.PrefixLength);
                v4.Add((network, mask));
            }
            else if (net.BaseAddress.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
            {
                var (network, mask) = ToV6(net.BaseAddress, net.PrefixLength);
                v6.Add((network, mask));
            }
        }
        _v4 = v4.ToArray();
        _v6 = v6.ToArray();
    }

    public int Count => _v4.Length + _v6.Length;

    public bool Contains(IPAddress address)
    {
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
        {
            var ip = ToUInt32(address);
            foreach (var (net, mask) in _v4)
                if ((ip & mask) == net) return true;
            return false;
        }
        if (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            var ip = ToUInt128(address);
            foreach (var (net, mask) in _v6)
                if ((ip & mask) == net) return true;
            return false;
        }
        return false;
    }

    public bool Contains(string address)
        => IPAddress.TryParse(address, out var ip) && Contains(ip);

    private static (uint Network, uint Mask) ToV4(IPAddress address, int prefixLength)
    {
        var mask = prefixLength == 0 ? 0u : 0xFFFF_FFFFu << (32 - prefixLength);
        var ip = ToUInt32(address) & mask;
        return (ip, mask);
    }

    private static (UInt128 Network, UInt128 Mask) ToV6(IPAddress address, int prefixLength)
    {
        var mask = prefixLength == 0 ? UInt128.Zero : UInt128.MaxValue << (128 - prefixLength);
        var ip = ToUInt128(address) & mask;
        return (ip, mask);
    }

    private static uint ToUInt32(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[4];
        if (!address.TryWriteBytes(bytes, out _)) return 0;
        return ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    private static UInt128 ToUInt128(IPAddress address)
    {
        Span<byte> bytes = stackalloc byte[16];
        if (!address.TryWriteBytes(bytes, out _)) return UInt128.Zero;
        UInt128 hi = 0, lo = 0;
        for (var i = 0; i < 8; i++) hi = (hi << 8) | bytes[i];
        for (var i = 8; i < 16; i++) lo = (lo << 8) | bytes[i];
        return (hi << 64) | lo;
    }
}
