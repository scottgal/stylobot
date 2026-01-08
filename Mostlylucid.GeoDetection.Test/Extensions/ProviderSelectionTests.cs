using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Extensions;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Test.Extensions;

/// <summary>
///     Tests for provider selection and convenience methods
/// </summary>
public class ProviderSelectionTests
{
    #region AddGeoRoutingWithIpApi Tests

    [Fact]
    public void AddGeoRoutingWithIpApi_SetsIpApiProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRoutingWithIpApi();

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GeoLite2Options>>().Value;
        Assert.Equal(GeoProvider.IpApi, options.Provider);
    }

    [Fact]
    public void AddGeoRoutingWithIpApi_WithRoutingOptions_ConfiguresRouting()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRoutingWithIpApi(options =>
        {
            options.BlockVpns = true;
            options.BlockedCountries = new[] { "KP" };
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var routingOptions = provider.GetRequiredService<IOptions<GeoRoutingOptions>>().Value;
        Assert.True(routingOptions.BlockVpns);
        Assert.Contains("KP", routingOptions.BlockedCountries!);
    }

    [Fact]
    public void AddGeoRoutingWithIpApi_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddGeoRoutingWithIpApi();

        // Assert
        Assert.Same(services, result);
    }

    #endregion

    #region AddGeoRoutingWithDataHub Tests

    [Fact]
    public void AddGeoRoutingWithDataHub_SetsDataHubProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRoutingWithDataHub();

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GeoLite2Options>>().Value;
        Assert.Equal(GeoProvider.DataHubCsv, options.Provider);
    }

    [Fact]
    public void AddGeoRoutingWithDataHub_WithRoutingOptions_ConfiguresRouting()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRoutingWithDataHub(options => { options.AllowedCountries = new[] { "US", "CA" }; });

        // Assert
        var provider = services.BuildServiceProvider();
        var routingOptions = provider.GetRequiredService<IOptions<GeoRoutingOptions>>().Value;
        Assert.Equal(2, routingOptions.AllowedCountries!.Length);
    }

    [Fact]
    public void AddGeoRoutingWithDataHub_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddGeoRoutingWithDataHub();

        // Assert
        Assert.Same(services, result);
    }

    #endregion

    #region AddGeoRoutingSimple Tests

    [Fact]
    public void AddGeoRoutingSimple_SetsSimpleProvider()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRoutingSimple();

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GeoLite2Options>>().Value;
        Assert.Equal(GeoProvider.Simple, options.Provider);
    }

    [Fact]
    public void AddGeoRoutingSimple_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddGeoRoutingSimple();

        // Assert
        Assert.Same(services, result);
    }

    #endregion

    #region Provider Registration Tests

    [Fact]
    public void AddGeoRouting_RegistersAllProviders()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();

        // Act
        services.AddGeoRouting();
        var provider = services.BuildServiceProvider();

        // Assert - All provider services should be registered
        Assert.NotNull(provider.GetService<SimpleGeoLocationService>());
        Assert.NotNull(provider.GetService<IpApiGeoLocationService>());
        Assert.NotNull(provider.GetService<DataHubGeoLocationService>());
        Assert.NotNull(provider.GetService<MaxMindGeoLocationService>());
    }

    [Fact]
    public void AddGeoRouting_DefaultProvider_IsMaxMindLocal()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRouting();

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GeoLite2Options>>().Value;
        Assert.Equal(GeoProvider.MaxMindLocal, options.Provider);
    }

    [Theory]
    [InlineData(GeoProvider.Simple)]
    [InlineData(GeoProvider.IpApi)]
    [InlineData(GeoProvider.DataHubCsv)]
    [InlineData(GeoProvider.MaxMindLocal)]
    public void AddGeoRouting_WithProviderOption_RegistersCorrectProvider(GeoProvider geoProvider)
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRouting(configureProvider: options => options.Provider = geoProvider);

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<GeoLite2Options>>().Value;
        Assert.Equal(geoProvider, options.Provider);
    }

    #endregion

    #region Cache Options Tests

    [Fact]
    public void AddGeoRouting_DefaultCacheOptions_DisablesDatabase()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRouting();

        // Assert
        var provider = services.BuildServiceProvider();
        var cacheOptions = provider.GetRequiredService<IOptions<GeoCacheOptions>>().Value;
        Assert.False(cacheOptions.Enabled);
    }

    [Fact]
    public void AddGeoRouting_WithCacheOptions_EnablesDatabase()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRouting(configureCache: options =>
        {
            options.Enabled = true;
            options.CacheExpiration = TimeSpan.FromDays(7);
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var cacheOptions = provider.GetRequiredService<IOptions<GeoCacheOptions>>().Value;
        Assert.True(cacheOptions.Enabled);
        Assert.Equal(TimeSpan.FromDays(7), cacheOptions.CacheExpiration);
    }

    #endregion

    #region IGeoLocationService Resolution Tests

    [Fact]
    public void AddGeoRouting_ResolvesIGeoLocationService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();

        // Act
        services.AddGeoRouting();

        // Assert
        var provider = services.BuildServiceProvider();
        var service = provider.GetService<IGeoLocationService>();
        Assert.NotNull(service);
    }

    [Fact]
    public void AddGeoRoutingWithIpApi_ResolvesIGeoLocationService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();

        // Act
        services.AddGeoRoutingWithIpApi();

        // Assert
        var provider = services.BuildServiceProvider();
        var service = provider.GetService<IGeoLocationService>();
        Assert.NotNull(service);
    }

    [Fact]
    public void AddGeoRoutingSimple_ResolvesIGeoLocationService()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();

        // Act
        services.AddGeoRoutingSimple();

        // Assert
        var provider = services.BuildServiceProvider();
        var service = provider.GetService<IGeoLocationService>();
        Assert.NotNull(service);
    }

    #endregion
}