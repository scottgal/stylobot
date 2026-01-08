using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Test.Models;

/// <summary>
///     Comprehensive tests for GeoBlockResult model
/// </summary>
public class GeoBlockResultTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_SetsDefaultIsBlockedToFalse()
    {
        // Act
        var result = new GeoBlockResult();

        // Assert
        Assert.False(result.IsBlocked);
    }

    [Fact]
    public void Constructor_SetsDefaultBlockReasonToNull()
    {
        // Act
        var result = new GeoBlockResult();

        // Assert
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public void Constructor_SetsDefaultLocationToNull()
    {
        // Act
        var result = new GeoBlockResult();

        // Assert
        Assert.Null(result.Location);
    }

    [Fact]
    public void Constructor_SetsDefaultIpAddressToNull()
    {
        // Act
        var result = new GeoBlockResult();

        // Assert
        Assert.Null(result.IpAddress);
    }

    #endregion

    #region Property Setting Tests

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsBlocked_CanBeSet(bool isBlocked)
    {
        // Arrange
        var result = new GeoBlockResult();

        // Act
        result.IsBlocked = isBlocked;

        // Assert
        Assert.Equal(isBlocked, result.IsBlocked);
    }

    [Theory]
    [InlineData("Country not allowed")]
    [InlineData("VPN detected")]
    [InlineData("Datacenter traffic blocked")]
    [InlineData("IP blacklisted")]
    public void BlockReason_CanBeSet(string reason)
    {
        // Arrange
        var result = new GeoBlockResult();

        // Act
        result.BlockReason = reason;

        // Assert
        Assert.Equal(reason, result.BlockReason);
    }

    [Fact]
    public void Location_CanBeSet()
    {
        // Arrange
        var result = new GeoBlockResult();
        var location = new GeoLocation
        {
            CountryCode = "CN",
            CountryName = "China"
        };

        // Act
        result.Location = location;

        // Assert
        Assert.NotNull(result.Location);
        Assert.Equal("CN", result.Location.CountryCode);
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("8.8.8.8")]
    [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334")]
    public void IpAddress_CanBeSet(string ipAddress)
    {
        // Arrange
        var result = new GeoBlockResult();

        // Act
        result.IpAddress = ipAddress;

        // Assert
        Assert.Equal(ipAddress, result.IpAddress);
    }

    #endregion

    #region Complete Result Tests

    [Fact]
    public void CompleteResult_BlockedByCountry()
    {
        // Act
        var result = new GeoBlockResult
        {
            IsBlocked = true,
            BlockReason = "Country CN is not in the allowed list",
            IpAddress = "123.45.67.89",
            Location = new GeoLocation
            {
                CountryCode = "CN",
                CountryName = "China",
                City = "Beijing"
            }
        };

        // Assert
        Assert.True(result.IsBlocked);
        Assert.Contains("CN", result.BlockReason);
        Assert.Equal("CN", result.Location!.CountryCode);
        Assert.NotNull(result.IpAddress);
    }

    [Fact]
    public void CompleteResult_BlockedByVpn()
    {
        // Act
        var result = new GeoBlockResult
        {
            IsBlocked = true,
            BlockReason = "VPN/Proxy traffic is not allowed",
            IpAddress = "10.20.30.40",
            Location = new GeoLocation
            {
                CountryCode = "NL",
                CountryName = "Netherlands",
                IsVpn = true
            }
        };

        // Assert
        Assert.True(result.IsBlocked);
        Assert.Contains("VPN", result.BlockReason);
        Assert.True(result.Location!.IsVpn);
    }

    [Fact]
    public void CompleteResult_BlockedByHosting()
    {
        // Act
        var result = new GeoBlockResult
        {
            IsBlocked = true,
            BlockReason = "Datacenter traffic is not allowed",
            IpAddress = "52.1.2.3",
            Location = new GeoLocation
            {
                CountryCode = "US",
                CountryName = "United States",
                IsHosting = true
            }
        };

        // Assert
        Assert.True(result.IsBlocked);
        Assert.Contains("Datacenter", result.BlockReason);
        Assert.True(result.Location!.IsHosting);
    }

    [Fact]
    public void CompleteResult_Allowed()
    {
        // Act
        var result = new GeoBlockResult
        {
            IsBlocked = false,
            BlockReason = null,
            IpAddress = "100.200.1.2",
            Location = new GeoLocation
            {
                CountryCode = "US",
                CountryName = "United States",
                City = "San Francisco",
                IsVpn = false,
                IsHosting = false
            }
        };

        // Assert
        Assert.False(result.IsBlocked);
        Assert.Null(result.BlockReason);
        Assert.Equal("US", result.Location!.CountryCode);
        Assert.False(result.Location.IsVpn);
        Assert.False(result.Location.IsHosting);
    }

    [Fact]
    public void CompleteResult_NoLocationInfo()
    {
        // Act
        var result = new GeoBlockResult
        {
            IsBlocked = false,
            BlockReason = null,
            IpAddress = "127.0.0.1",
            Location = null
        };

        // Assert
        Assert.False(result.IsBlocked);
        Assert.Null(result.Location);
        Assert.Equal("127.0.0.1", result.IpAddress);
    }

    #endregion
}