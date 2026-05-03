using Mostlylucid.BotDetection.Helpers;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Tests for CIDR IP range matching functionality
/// </summary>
public class CidrMatchingTests
{
    private static bool IsIpInRange(string ipAddress, string cidr)
    {
        return CidrHelper.IsInSubnet(ipAddress, cidr);
    }

    #region Full Octet Boundary Tests (/8, /16, /24)

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.0/24", true)]
    [InlineData("192.168.1.254", "192.168.1.0/24", true)]
    [InlineData("192.168.2.1", "192.168.1.0/24", false)]
    [InlineData("192.168.0.1", "192.168.1.0/24", false)]
    public void IsIpInRange_Slash24_MatchesThreeOctets(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.0/16", true)]
    [InlineData("10.0.255.255", "10.0.0.0/16", true)]
    [InlineData("10.1.0.1", "10.0.0.0/16", false)]
    [InlineData("11.0.0.1", "10.0.0.0/16", false)]
    public void IsIpInRange_Slash16_MatchesTwoOctets(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("10.0.0.1", "10.0.0.0/8", true)]
    [InlineData("10.255.255.255", "10.0.0.0/8", true)]
    [InlineData("11.0.0.1", "10.0.0.0/8", false)]
    public void IsIpInRange_Slash8_MatchesFirstOctet(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    #endregion

    #region Non-Octet Boundary Tests (the fixed bug)

    [Theory]
    [InlineData("192.168.0.1", "192.168.0.0/23", true)] // First subnet
    [InlineData("192.168.0.254", "192.168.0.0/23", true)]
    [InlineData("192.168.1.1", "192.168.0.0/23", true)] // Second subnet
    [InlineData("192.168.1.254", "192.168.0.0/23", true)]
    [InlineData("192.168.2.1", "192.168.0.0/23", false)] // Outside range
    [InlineData("192.168.3.1", "192.168.0.0/23", false)]
    public void IsIpInRange_Slash23_MatchesTwoSubnets(string ip, string cidr, bool expected)
    {
        // /23 = first 23 bits, which means 3rd octet can be 0 or 1 (for 192.168.0.0/23)
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("192.168.1.0", "192.168.1.0/25", true)] // First half
    [InlineData("192.168.1.127", "192.168.1.0/25", true)]
    [InlineData("192.168.1.128", "192.168.1.0/25", false)] // Second half (different subnet)
    [InlineData("192.168.1.255", "192.168.1.0/25", false)]
    public void IsIpInRange_Slash25_MatchesHalfSubnet(string ip, string cidr, bool expected)
    {
        // /25 = first 25 bits, which means last octet 0-127 for .0/25
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("192.168.1.128", "192.168.1.128/25", true)] // Second half
    [InlineData("192.168.1.255", "192.168.1.128/25", true)]
    [InlineData("192.168.1.0", "192.168.1.128/25", false)] // First half (different subnet)
    [InlineData("192.168.1.127", "192.168.1.128/25", false)]
    public void IsIpInRange_Slash25_SecondHalf_MatchesCorrectly(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("10.0.0.0", "10.0.0.0/22", true)]
    [InlineData("10.0.1.1", "10.0.0.0/22", true)]
    [InlineData("10.0.2.1", "10.0.0.0/22", true)]
    [InlineData("10.0.3.255", "10.0.0.0/22", true)]
    [InlineData("10.0.4.0", "10.0.0.0/22", false)]
    public void IsIpInRange_Slash22_MatchesFourSubnets(string ip, string cidr, bool expected)
    {
        // /22 = 4 subnets (1024 addresses)
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("172.16.0.0", "172.16.0.0/20", true)]
    [InlineData("172.16.15.255", "172.16.0.0/20", true)]
    [InlineData("172.16.16.0", "172.16.0.0/20", false)]
    public void IsIpInRange_Slash20_Matches16Subnets(string ip, string cidr, bool expected)
    {
        // /20 = 16 subnets in third octet
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    #endregion

    #region Edge Cases

    [Theory]
    [InlineData("0.0.0.0", "0.0.0.0/0", true)]
    [InlineData("255.255.255.255", "0.0.0.0/0", true)]
    [InlineData("192.168.1.1", "0.0.0.0/0", true)]
    public void IsIpInRange_Slash0_MatchesAllAddresses(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.1/32", true)]
    [InlineData("192.168.1.2", "192.168.1.1/32", false)]
    public void IsIpInRange_Slash32_MatchesExactAddress(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("192.168.1.1", "192.168.1.1", false)] // Missing prefix
    [InlineData("192.168.1.1", "invalid", false)]
    [InlineData("192.168.1.1", "", false)]
    [InlineData("192.168.1.1", "192.168.1.0/abc", false)]
    [InlineData("invalid", "192.168.1.0/24", false)]
    [InlineData("", "192.168.1.0/24", false)]
    public void IsIpInRange_InvalidInput_ReturnsFalse(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    #endregion

    #region AWS/Azure/GCP IP Ranges (Real-world examples)

    [Theory]
    [InlineData("3.0.0.1", "3.0.0.0/15", true)] // AWS
    [InlineData("3.1.255.255", "3.0.0.0/15", true)]
    [InlineData("3.2.0.0", "3.0.0.0/15", false)]
    public void IsIpInRange_AwsRange_Slash15(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("20.33.0.1", "20.33.0.0/16", true)] // Azure
    [InlineData("20.33.255.255", "20.33.0.0/16", true)]
    [InlineData("20.34.0.1", "20.33.0.0/16", false)]
    public void IsIpInRange_AzureRange_Slash16(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    [Theory]
    [InlineData("34.64.0.1", "34.64.0.0/10", true)] // GCP
    [InlineData("34.127.255.255", "34.64.0.0/10", true)]
    [InlineData("34.128.0.1", "34.64.0.0/10", false)]
    public void IsIpInRange_GcpRange_Slash10(string ip, string cidr, bool expected)
    {
        Assert.Equal(expected, IsIpInRange(ip, cidr));
    }

    #endregion

    #region Specific Bug Fix Validation

    [Fact]
    public void IsIpInRange_PreviousBugCase_Slash23_NowWorksCorrectly()
    {
        // This test validates the specific bug that was fixed
        // Old implementation: prefix / 8 = 23 / 8 = 2 octets checked
        // New implementation: checks 23 bits (2 full octets + 7 bits of third)

        // 192.168.0.0/23 should match:
        // - 192.168.0.x (third octet = 0, binary 00000000)
        // - 192.168.1.x (third octet = 1, binary 00000001)
        // But NOT:
        // - 192.168.2.x (third octet = 2, binary 00000010 - differs in 7th bit from network)

        Assert.True(IsIpInRange("192.168.0.50", "192.168.0.0/23"));
        Assert.True(IsIpInRange("192.168.1.50", "192.168.0.0/23"));
        Assert.False(IsIpInRange("192.168.2.50", "192.168.0.0/23"));
    }

    [Fact]
    public void IsIpInRange_PreviousBugCase_Slash25_NowWorksCorrectly()
    {
        // This test validates another specific bug case
        // Old implementation: prefix / 8 = 25 / 8 = 3 octets checked (ignoring remaining bit!)
        // New implementation: checks 25 bits (3 full octets + 1 bit of fourth)

        // 192.168.1.0/25 should match 192.168.1.0-127 only (bit 7 of last octet = 0)
        Assert.True(IsIpInRange("192.168.1.0", "192.168.1.0/25"));
        Assert.True(IsIpInRange("192.168.1.64", "192.168.1.0/25"));
        Assert.True(IsIpInRange("192.168.1.127", "192.168.1.0/25"));

        // Should NOT match 192.168.1.128-255 (bit 7 of last octet = 1)
        Assert.False(IsIpInRange("192.168.1.128", "192.168.1.0/25"));
        Assert.False(IsIpInRange("192.168.1.200", "192.168.1.0/25"));
        Assert.False(IsIpInRange("192.168.1.255", "192.168.1.0/25"));
    }

    #endregion
}