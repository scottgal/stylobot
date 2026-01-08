using Mostlylucid.GeoDetection.Models;

namespace Mostlylucid.GeoDetection.Test.Models;

/// <summary>
///     Comprehensive tests for GeoLocation model
/// </summary>
public class GeoLocationTests
{
    #region Constructor Tests

    [Fact]
    public void Constructor_SetsDefaultCountryCodeToEmpty()
    {
        // Act
        var location = new GeoLocation();

        // Assert
        Assert.Equal("", location.CountryCode);
    }

    [Fact]
    public void Constructor_SetsDefaultCountryNameToEmpty()
    {
        // Act
        var location = new GeoLocation();

        // Assert
        Assert.Equal("", location.CountryName);
    }

    [Fact]
    public void Constructor_SetsOptionalPropertiesToNull()
    {
        // Act
        var location = new GeoLocation();

        // Assert
        Assert.Null(location.ContinentCode);
        Assert.Null(location.RegionCode);
        Assert.Null(location.City);
        Assert.Null(location.Latitude);
        Assert.Null(location.Longitude);
        Assert.Null(location.TimeZone);
    }

    [Fact]
    public void Constructor_SetsDefaultBooleanPropertiesToFalse()
    {
        // Act
        var location = new GeoLocation();

        // Assert
        Assert.False(location.IsVpn);
        Assert.False(location.IsHosting);
    }

    #endregion

    #region Property Setting Tests

    [Theory]
    [InlineData("US")]
    [InlineData("GB")]
    [InlineData("CN")]
    [InlineData("JP")]
    [InlineData("DE")]
    public void CountryCode_CanBeSet(string countryCode)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.CountryCode = countryCode;

        // Assert
        Assert.Equal(countryCode, location.CountryCode);
    }

    [Theory]
    [InlineData("United States")]
    [InlineData("United Kingdom")]
    [InlineData("China")]
    [InlineData("Japan")]
    public void CountryName_CanBeSet(string countryName)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.CountryName = countryName;

        // Assert
        Assert.Equal(countryName, location.CountryName);
    }

    [Theory]
    [InlineData("NA")]
    [InlineData("EU")]
    [InlineData("AS")]
    [InlineData("AF")]
    [InlineData("SA")]
    [InlineData("OC")]
    [InlineData("AN")]
    public void ContinentCode_CanBeSet(string continentCode)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.ContinentCode = continentCode;

        // Assert
        Assert.Equal(continentCode, location.ContinentCode);
    }

    [Theory]
    [InlineData("CA")]
    [InlineData("NY")]
    [InlineData("TX")]
    public void RegionCode_CanBeSet(string regionCode)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.RegionCode = regionCode;

        // Assert
        Assert.Equal(regionCode, location.RegionCode);
    }

    [Theory]
    [InlineData("San Francisco")]
    [InlineData("New York")]
    [InlineData("London")]
    [InlineData("Tokyo")]
    public void City_CanBeSet(string city)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.City = city;

        // Assert
        Assert.Equal(city, location.City);
    }

    [Theory]
    [InlineData(37.7749)]
    [InlineData(-33.8688)]
    [InlineData(51.5074)]
    [InlineData(0.0)]
    [InlineData(-90.0)]
    [InlineData(90.0)]
    public void Latitude_CanBeSet(double latitude)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.Latitude = latitude;

        // Assert
        Assert.Equal(latitude, location.Latitude);
    }

    [Theory]
    [InlineData(-122.4194)]
    [InlineData(151.2093)]
    [InlineData(-0.1278)]
    [InlineData(0.0)]
    [InlineData(-180.0)]
    [InlineData(180.0)]
    public void Longitude_CanBeSet(double longitude)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.Longitude = longitude;

        // Assert
        Assert.Equal(longitude, location.Longitude);
    }

    [Theory]
    [InlineData("America/Los_Angeles")]
    [InlineData("America/New_York")]
    [InlineData("Europe/London")]
    [InlineData("Asia/Tokyo")]
    [InlineData("UTC")]
    public void TimeZone_CanBeSet(string timeZone)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.TimeZone = timeZone;

        // Assert
        Assert.Equal(timeZone, location.TimeZone);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsVpn_CanBeSet(bool isVpn)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.IsVpn = isVpn;

        // Assert
        Assert.Equal(isVpn, location.IsVpn);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsHosting_CanBeSet(bool isHosting)
    {
        // Arrange
        var location = new GeoLocation();

        // Act
        location.IsHosting = isHosting;

        // Assert
        Assert.Equal(isHosting, location.IsHosting);
    }

    #endregion

    #region Complete Location Tests

    [Fact]
    public void CompleteLocation_UnitedStates()
    {
        // Act
        var location = new GeoLocation
        {
            CountryCode = "US",
            CountryName = "United States",
            ContinentCode = "NA",
            RegionCode = "CA",
            City = "San Francisco",
            Latitude = 37.7749,
            Longitude = -122.4194,
            TimeZone = "America/Los_Angeles",
            IsVpn = false,
            IsHosting = false
        };

        // Assert
        Assert.Equal("US", location.CountryCode);
        Assert.Equal("United States", location.CountryName);
        Assert.Equal("NA", location.ContinentCode);
        Assert.Equal("CA", location.RegionCode);
        Assert.Equal("San Francisco", location.City);
        Assert.Equal(37.7749, location.Latitude);
        Assert.Equal(-122.4194, location.Longitude);
        Assert.Equal("America/Los_Angeles", location.TimeZone);
        Assert.False(location.IsVpn);
        Assert.False(location.IsHosting);
    }

    [Fact]
    public void CompleteLocation_VpnUser()
    {
        // Act
        var location = new GeoLocation
        {
            CountryCode = "NL",
            CountryName = "Netherlands",
            ContinentCode = "EU",
            City = "Amsterdam",
            IsVpn = true,
            IsHosting = false
        };

        // Assert
        Assert.True(location.IsVpn);
        Assert.False(location.IsHosting);
    }

    [Fact]
    public void CompleteLocation_DatacenterTraffic()
    {
        // Act
        var location = new GeoLocation
        {
            CountryCode = "US",
            CountryName = "United States",
            ContinentCode = "NA",
            City = "Ashburn",
            IsVpn = false,
            IsHosting = true
        };

        // Assert
        Assert.False(location.IsVpn);
        Assert.True(location.IsHosting);
    }

    [Fact]
    public void MinimalLocation_OnlyCountryCode()
    {
        // Act
        var location = new GeoLocation
        {
            CountryCode = "XX"
        };

        // Assert
        Assert.Equal("XX", location.CountryCode);
        Assert.Equal("", location.CountryName);
        Assert.Null(location.City);
    }

    #endregion
}