using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq.Protected;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Test.Services;

/// <summary>
///     Tests for IpApiGeoLocationService (ip-api.com)
/// </summary>
public class IpApiGeoLocationServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<IpApiGeoLocationService>> _loggerMock;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptions<GeoLite2Options> _options;

    public IpApiGeoLocationServiceTests()
    {
        _loggerMock = new Mock<ILogger<IpApiGeoLocationService>>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _options = Options.Create(new GeoLite2Options
        {
            Provider = GeoProvider.IpApi,
            CacheDuration = TimeSpan.FromMinutes(5)
        });
    }

    [Fact]
    public void Constructor_DoesNotThrow()
    {
        // Arrange & Act
        var service = CreateService();

        // Assert
        Assert.NotNull(service);
    }

    [Fact]
    public async Task GetLocationAsync_ValidResponse_ReturnsGeoLocation()
    {
        // Arrange
        var apiResponse = new
        {
            status = "success",
            country = "United States",
            countryCode = "US",
            region = "CA",
            regionName = "California",
            city = "Mountain View",
            zip = "94043",
            lat = 37.4056,
            lon = -122.0775,
            timezone = "America/Los_Angeles",
            isp = "Google LLC",
            hosting = false
        };

        var service = CreateServiceWithResponse(apiResponse);

        // Act
        var result = await service.GetLocationAsync("8.8.8.8");

        // Assert
        Assert.NotNull(result);
        Assert.Equal("US", result.CountryCode);
        Assert.Equal("United States", result.CountryName);
        Assert.Equal("CA", result.RegionCode);
        Assert.Equal("Mountain View", result.City);
        Assert.Equal(37.4056, result.Latitude);
        Assert.Equal(-122.0775, result.Longitude);
        Assert.Equal("America/Los_Angeles", result.TimeZone);
    }

    [Fact]
    public async Task GetLocationAsync_PrivateIp_ReturnsPrivateNetwork()
    {
        // Arrange - Private IPs are handled locally, not sent to API
        var service = CreateService();

        // Act
        var result = await service.GetLocationAsync("192.168.1.1");

        // Assert - Private IPs return a special "Private Network" location
        Assert.NotNull(result);
        Assert.Equal("XX", result.CountryCode);
        Assert.Equal("Private Network", result.CountryName);
    }

    [Fact]
    public async Task GetLocationAsync_FailedApiResponse_ReturnsNull()
    {
        // Arrange - API returns failure for invalid IPs
        var apiResponse = new
        {
            status = "fail",
            message = "invalid query"
        };

        var service = CreateServiceWithResponse(apiResponse);

        // Act
        var result = await service.GetLocationAsync("8.8.8.8");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetLocationAsync_InvalidIp_ReturnsNull()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = await service.GetLocationAsync("not-an-ip");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetLocationAsync_HttpError_ReturnsNull()
    {
        // Arrange
        var service = CreateServiceWithHttpError(HttpStatusCode.ServiceUnavailable);

        // Act
        var result = await service.GetLocationAsync("8.8.8.8");

        // Assert
        Assert.Null(result);
    }

    [Fact]
    public async Task GetLocationAsync_CachesResults()
    {
        // Arrange
        var apiResponse = new
        {
            status = "success",
            countryCode = "US",
            country = "United States"
        };

        var service = CreateServiceWithResponse(apiResponse);
        var ip = "8.8.8.8";

        // Act - First call
        var result1 = await service.GetLocationAsync(ip);
        var stats1 = service.GetStatistics();

        // Second call (should hit cache)
        var result2 = await service.GetLocationAsync(ip);
        var stats2 = service.GetStatistics();

        // Assert
        Assert.NotNull(result1);
        Assert.NotNull(result2);
        Assert.Equal(2, stats2.TotalLookups);
        Assert.Equal(1, stats2.CacheHits);
    }

    [Fact]
    public async Task IsFromCountryAsync_MatchingCountry_ReturnsTrue()
    {
        // Arrange
        var apiResponse = new
        {
            status = "success",
            countryCode = "US",
            country = "United States"
        };

        var service = CreateServiceWithResponse(apiResponse);

        // Act
        var result = await service.IsFromCountryAsync("8.8.8.8", "US");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public async Task IsFromCountryAsync_DifferentCountry_ReturnsFalse()
    {
        // Arrange
        var apiResponse = new
        {
            status = "success",
            countryCode = "US",
            country = "United States"
        };

        var service = CreateServiceWithResponse(apiResponse);

        // Act
        var result = await service.IsFromCountryAsync("8.8.8.8", "GB");

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task IsFromCountryAsync_CaseInsensitive()
    {
        // Arrange
        var apiResponse = new
        {
            status = "success",
            countryCode = "US",
            country = "United States"
        };

        var service = CreateServiceWithResponse(apiResponse);

        // Act
        var result = await service.IsFromCountryAsync("8.8.8.8", "us");

        // Assert
        Assert.True(result);
    }

    [Fact]
    public void GetStatistics_ReturnsValidStatistics()
    {
        // Arrange
        var service = CreateService();

        // Act
        var stats = service.GetStatistics();

        // Assert
        Assert.NotNull(stats);
        Assert.Equal(0, stats.TotalLookups);
        Assert.Equal(0, stats.CacheHits);
    }

    [Fact]
    public async Task GetLocationAsync_HostingDetected_SetsIsHosting()
    {
        // Arrange
        var apiResponse = new
        {
            status = "success",
            countryCode = "US",
            country = "United States",
            hosting = true
        };

        var service = CreateServiceWithResponse(apiResponse);

        // Act
        var result = await service.GetLocationAsync("1.2.3.4");

        // Assert
        Assert.NotNull(result);
        Assert.True(result.IsHosting);
    }

    private IpApiGeoLocationService CreateService()
    {
        return CreateServiceWithResponse(new { status = "fail" });
    }

    private IpApiGeoLocationService CreateServiceWithResponse(object apiResponse)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent(JsonSerializer.Serialize(apiResponse))
            });

        var httpClient = new HttpClient(mockHandler.Object);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("IpApi"))
            .Returns(httpClient);

        return new IpApiGeoLocationService(
            _loggerMock.Object,
            _options,
            _httpClientFactoryMock.Object,
            _memoryCache);
    }

    private IpApiGeoLocationService CreateServiceWithHttpError(HttpStatusCode statusCode)
    {
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = statusCode
            });

        var httpClient = new HttpClient(mockHandler.Object);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("IpApi"))
            .Returns(httpClient);

        return new IpApiGeoLocationService(
            _loggerMock.Object,
            _options,
            _httpClientFactoryMock.Object,
            _memoryCache);
    }
}