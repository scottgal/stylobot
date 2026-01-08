using System.Net;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq.Protected;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Test.Services;

/// <summary>
///     Tests for DataHubGeoLocationService (CSV-based IP lookup)
///     Note: Many tests require database to be loaded. Without it, lookups return null.
/// </summary>
public class DataHubGeoLocationServiceTests
{
    private readonly Mock<IHttpClientFactory> _httpClientFactoryMock;
    private readonly Mock<ILogger<DataHubGeoLocationService>> _loggerMock;
    private readonly IMemoryCache _memoryCache;
    private readonly IOptions<GeoLite2Options> _options;

    public DataHubGeoLocationServiceTests()
    {
        _loggerMock = new Mock<ILogger<DataHubGeoLocationService>>();
        _httpClientFactoryMock = new Mock<IHttpClientFactory>();
        _memoryCache = new MemoryCache(new MemoryCacheOptions());
        _options = Options.Create(new GeoLite2Options
        {
            Provider = GeoProvider.DataHubCsv,
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
    public async Task GetLocationAsync_WithoutDatabase_ReturnsNull()
    {
        // DataHub service requires database to be loaded
        // Without database, all lookups return null
        var service = CreateService();

        // Act
        var result = await service.GetLocationAsync("8.8.8.8");

        // Assert - Returns null because database isn't loaded
        Assert.Null(result);
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
    public async Task GetLocationAsync_IncrementsStatistics()
    {
        // Arrange
        var service = CreateService();

        // Act
        await service.GetLocationAsync("8.8.8.8");
        await service.GetLocationAsync("1.1.1.1");
        var stats = service.GetStatistics();

        // Assert - Lookups are counted even if they fail
        Assert.Equal(2, stats.TotalLookups);
    }

    [Fact]
    public async Task IsFromCountryAsync_WithoutDatabase_ReturnsFalse()
    {
        // Arrange
        var service = CreateService();

        // Act
        var result = await service.IsFromCountryAsync("8.8.8.8", "US");

        // Assert - Without database, we can't determine country
        Assert.False(result);
    }

    [Fact]
    public async Task StartAsync_DoesNotThrow()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert
        await service.StartAsync(CancellationToken.None);
    }

    [Fact]
    public async Task StopAsync_DoesNotThrow()
    {
        // Arrange
        var service = CreateService();

        // Act & Assert
        await service.StopAsync(CancellationToken.None);
    }

    private DataHubGeoLocationService CreateService()
    {
        // Setup mock HTTP client that returns empty response
        var mockHandler = new Mock<HttpMessageHandler>();
        mockHandler.Protected()
            .Setup<Task<HttpResponseMessage>>(
                "SendAsync",
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>())
            .ReturnsAsync(new HttpResponseMessage
            {
                StatusCode = HttpStatusCode.OK,
                Content = new StringContent("")
            });

        var httpClient = new HttpClient(mockHandler.Object);
        _httpClientFactoryMock
            .Setup(f => f.CreateClient("DataHub"))
            .Returns(httpClient);

        return new DataHubGeoLocationService(
            _loggerMock.Object,
            _options,
            _httpClientFactoryMock.Object,
            _memoryCache);
    }
}