using System.Net;
using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.Test.ThreatIntel;

public class IpCidrCacheTests
{
    [Theory]
    [InlineData("192.0.2.0/24", "192.0.2.1", true)]
    [InlineData("192.0.2.0/24", "192.0.2.255", true)]
    [InlineData("192.0.2.0/24", "192.0.3.1", false)]
    [InlineData("10.0.0.0/8", "10.255.255.255", true)]
    [InlineData("10.0.0.0/8", "11.0.0.1", false)]
    [InlineData("203.0.113.0/24", "203.0.113.0", true)]
    [InlineData("0.0.0.0/0", "1.2.3.4", true)]      // catch-all
    public void Contains_Ipv4_MatchesPrefix(string cidr, string ip, bool expected)
    {
        var cache = new IpCidrCache(new[] { cidr });
        Assert.Equal(expected, cache.Contains(ip));
    }

    [Theory]
    [InlineData("2001:db8::/32", "2001:db8::1", true)]
    [InlineData("2001:db8::/32", "2001:db8:ffff:ffff::1", true)]
    [InlineData("2001:db8::/32", "2001:db9::1", false)]
    [InlineData("::/0", "2001:db8::1", true)]
    public void Contains_Ipv6_MatchesPrefix(string cidr, string ip, bool expected)
    {
        var cache = new IpCidrCache(new[] { cidr });
        Assert.Equal(expected, cache.Contains(ip));
    }

    [Fact]
    public void Contains_MultipleCidrs_OrdersIndependent()
    {
        var cache = new IpCidrCache(new[] { "192.0.2.0/24", "203.0.113.0/24", "2001:db8::/32" });
        Assert.True(cache.Contains("192.0.2.10"));
        Assert.True(cache.Contains("203.0.113.99"));
        Assert.True(cache.Contains("2001:db8::abcd"));
        Assert.False(cache.Contains("198.51.100.1"));
    }

    [Fact]
    public void Contains_InvalidCidrEntries_Skipped()
    {
        // Garbage entries don't throw and don't poison the cache.
        var cache = new IpCidrCache(new[]
        {
            "192.0.2.0/24",
            "not-a-cidr",
            "999.999.999.999/24",
            "",
            "192.0.2.5/33"   // invalid prefix length
        });
        Assert.True(cache.Contains("192.0.2.5"));
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Contains_InvalidLookup_ReturnsFalseWithoutThrowing()
    {
        var cache = new IpCidrCache(new[] { "192.0.2.0/24" });
        Assert.False(cache.Contains("not-an-ip"));
    }

    [Fact]
    public void EmptyCache_NeverHits()
    {
        var cache = new IpCidrCache(Array.Empty<string>());
        Assert.False(cache.Contains("1.2.3.4"));
        Assert.False(cache.Contains(IPAddress.Loopback));
        Assert.Equal(0, cache.Count);
    }
}
