using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Tests for IPv4-mapped IPv6 subnet normalization.
///     Local/private IPs return empty (they must never get subnet reputation).
///     Public IPs return their /24 or /48 subnet.
/// </summary>
public class IPv4MappedSubnetTests
{
    [Theory]
    [InlineData("8.8.8.8", "8.8.8.0/24")]
    [InlineData("1.2.3.4", "1.2.3.0/24")]
    [InlineData("203.0.113.50", "203.0.113.0/24")]
    public void NormalizeIpToRange_PublicIPv4_Returns24Subnet(string ip, string expected)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("192.168.1.100")]
    [InlineData("10.0.0.1")]
    [InlineData("127.0.0.1")]
    [InlineData("172.16.0.1")]
    public void NormalizeIpToRange_PrivateIPv4_ReturnsEmpty(string ip)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(string.Empty, result);
    }

    [Theory]
    [InlineData("::ffff:8.8.8.8", "8.8.8.0/24")]
    public void NormalizeIpToRange_IPv4Mapped_PublicIP_ExtractsAndReturns24(string ip, string expected)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("::ffff:192.168.1.100")]
    [InlineData("::ffff:10.0.0.1")]
    [InlineData("::ffff:127.0.0.1")]
    public void NormalizeIpToRange_IPv4Mapped_PrivateIP_ReturnsEmpty(string ip)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(string.Empty, result);
    }

    [Fact]
    public void NormalizeIpToRange_IPv4Mapped_DoesNotReturn48Subnet()
    {
        // Public IPv4-mapped should return /24, not /48
        var result = SignatureFeedbackHandler.NormalizeIpToRange("::ffff:8.8.8.8");
        Assert.DoesNotContain("/48", result);
        Assert.Equal("8.8.8.0/24", result);
    }

    [Fact]
    public void NormalizeIpToRange_DifferentIPv4Mapped_DifferentSubnets()
    {
        // Private IPs return empty (no reputation)
        var local = SignatureFeedbackHandler.NormalizeIpToRange("::ffff:127.0.0.1");
        Assert.Equal(string.Empty, local);

        // Public IPs should be in different subnets
        var public1 = SignatureFeedbackHandler.NormalizeIpToRange("::ffff:8.8.8.8");
        var public2 = SignatureFeedbackHandler.NormalizeIpToRange("::ffff:1.1.1.1");
        Assert.NotEqual(public1, public2);
    }

    [Fact]
    public void NormalizeIpToRange_Localhost_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, SignatureFeedbackHandler.NormalizeIpToRange("::1"));
        Assert.Equal(string.Empty, SignatureFeedbackHandler.NormalizeIpToRange("127.0.0.1"));
    }

    [Theory]
    [InlineData("2001:db8:1234::1", "2001:db8:1234::/48")]
    public void NormalizeIpToRange_PureIPv6_Public_Returns48Subnet(string ip, string expected)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData("fe80::1")]
    [InlineData("fc00::1")]
    [InlineData("fd00::1")]
    public void NormalizeIpToRange_IPv6_LinkLocalAndULA_ReturnsEmpty(string ip)
    {
        var result = SignatureFeedbackHandler.NormalizeIpToRange(ip);
        Assert.Equal(string.Empty, result);
    }
}
