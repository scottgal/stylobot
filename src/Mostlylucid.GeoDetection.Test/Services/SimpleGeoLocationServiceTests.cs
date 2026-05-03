using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Test.Services;

/// <summary>
///     Tests for SimpleGeoLocationService (mock/testing service)
/// </summary>
public class SimpleGeoLocationServiceTests
{
    private readonly SimpleGeoLocationService _service;

    public SimpleGeoLocationServiceTests()
    {
        _service = new SimpleGeoLocationService();
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        // Assert
        Assert.NotNull(_service);
    }

    [Theory]
    [InlineData("8.8.8.8")] // Google DNS
    [InlineData("1.1.1.1")] // Cloudflare
    [InlineData("208.67.222.222")] // OpenDNS
    public async Task GetLocationAsync_ValidPublicIp_ReturnsLocation(string ip)
    {
        // Act
        var result = await _service.GetLocationAsync(ip);

        // Assert
        Assert.NotNull(result);
        Assert.False(string.IsNullOrEmpty(result.CountryCode));
        Assert.False(string.IsNullOrEmpty(result.CountryName));
    }

    [Theory]
    [InlineData("192.168.1.1")]
    [InlineData("10.0.0.1")]
    [InlineData("172.16.0.1")]
    [InlineData("127.0.0.1")]
    public async Task GetLocationAsync_PrivateIp_ReturnsLocation(string ip)
    {
        // SimpleGeoLocationService is a mock service that doesn't handle private IPs specially
        // It returns a location based on the first octet
        // Act
        var result = await _service.GetLocationAsync(ip);

        // Assert - Returns a location (mock behavior)
        Assert.NotNull(result);
        Assert.NotNull(result.CountryCode);
    }

    [Fact]
    public async Task GetLocationAsync_CachesResults()
    {
        // Arrange
        var ip = "8.8.8.8";

        // Act - First call
        var result1 = await _service.GetLocationAsync(ip);
        var stats1 = _service.GetStatistics();

        // Second call (should hit cache)
        var result2 = await _service.GetLocationAsync(ip);
        var stats2 = _service.GetStatistics();

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(result1.CountryCode, result2.CountryCode);
        Assert.Equal(2, stats2.TotalLookups);
        Assert.Equal(1, stats2.CacheHits);
    }

    [Fact]
    public async Task IsFromCountryAsync_ReturnsCorrectResult()
    {
        // Arrange - 8.x.x.x returns US in the mock service
        var ip = "8.8.8.8";

        // Act
        var result = await _service.IsFromCountryAsync(ip, "US");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsFromCountryAsync_DifferentCountry_ReturnsFalse()
    {
        // Arrange - 8.x.x.x returns US in the mock service
        var ip = "8.8.8.8";

        // Act
        var result = await _service.IsFromCountryAsync(ip, "GB");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public void GetStatistics_ReturnsStatistics()
    {
        // Act
        var stats = _service.GetStatistics();

        // Assert
        Assert.NotNull(stats);
    }

    [Fact]
    public async Task GetStatistics_TracksLookups()
    {
        // Act
        await _service.GetLocationAsync("8.8.8.8");
        await _service.GetLocationAsync("1.1.1.1");
        var stats = _service.GetStatistics();

        // Assert
        Assert.True(stats.TotalLookups >= 2);
    }

    [Theory]
    [InlineData("3.0.0.1", "US")] // AWS ranges
    [InlineData("1.0.0.1", "CN")] // China ranges
    [InlineData("51.0.0.1", "GB")] // UK ranges
    public async Task GetLocationAsync_ReturnsExpectedCountryBasedOnOctet(string ip, string expectedCountry)
    {
        // Act
        var result = await _service.GetLocationAsync(ip);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(expectedCountry, result.CountryCode);
    }
}