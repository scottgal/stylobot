using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Extensions;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Test.Extensions;

/// <summary>
///     Comprehensive tests for GeoDetection ServiceCollectionExtensions
/// </summary>
public class ServiceCollectionExtensionsTests
{
    #region Chaining Tests

    [Fact]
    public void AddGeoRouting_CanBeChained()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act - Chain multiple registrations
        services
            .AddLogging()
            .AddMemoryCache()
            .AddGeoRouting(options => options.BlockVpns = true);

        // Assert
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IGeoLocationService>());
    }

    #endregion

    #region AddGeoRouting Tests

    [Fact]
    public void AddGeoRouting_RegistersServices()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRouting();

        // Assert
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public void AddGeoRouting_RegistersOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRouting();

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.NotNull(options);
    }

    [Fact]
    public void AddGeoRouting_WithOptions_ConfiguresOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRouting(options =>
        {
            options.Enabled = true;
            options.BlockVpns = true;
            options.BlockedStatusCode = 403;
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.True(options!.Value.Enabled);
        Assert.True(options.Value.BlockVpns);
        Assert.Equal(403, options.Value.BlockedStatusCode);
    }

    [Fact]
    public void AddGeoRouting_RegistersGeoLocationService()
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
    public void AddGeoRouting_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.AddGeoRouting();

        // Assert
        Assert.Same(services, result);
    }

    [Fact]
    public void AddGeoRouting_NullOptions_UsesDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.AddGeoRouting();

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.True(options!.Value.Enabled); // Default
        Assert.Equal(451, options.Value.BlockedStatusCode); // Default
    }

    #endregion

    #region RestrictSiteToCountries Tests

    [Fact]
    public void RestrictSiteToCountries_SetsAllowedCountries()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RestrictSiteToCountries("US", "CA", "GB");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.NotNull(options!.Value.AllowedCountries);
        Assert.Equal(3, options.Value.AllowedCountries.Length);
        Assert.Contains("US", options.Value.AllowedCountries);
        Assert.Contains("CA", options.Value.AllowedCountries);
        Assert.Contains("GB", options.Value.AllowedCountries);
    }

    [Fact]
    public void RestrictSiteToCountries_EnablesGeoRouting()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RestrictSiteToCountries("US");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.True(options!.Value.Enabled);
    }

    [Fact]
    public void RestrictSiteToCountries_SingleCountry()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.RestrictSiteToCountries("US");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.Single(options!.Value.AllowedCountries!);
    }

    [Fact]
    public void RestrictSiteToCountries_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.RestrictSiteToCountries("US", "CA");

        // Assert
        Assert.Same(services, result);
    }

    #endregion

    #region BlockCountries Tests

    [Fact]
    public void BlockCountries_SetsBlockedCountries()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.BlockCountries("KP", "IR", "SY");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.NotNull(options!.Value.BlockedCountries);
        Assert.Equal(3, options.Value.BlockedCountries.Length);
        Assert.Contains("KP", options.Value.BlockedCountries);
        Assert.Contains("IR", options.Value.BlockedCountries);
        Assert.Contains("SY", options.Value.BlockedCountries);
    }

    [Fact]
    public void BlockCountries_EnablesGeoRouting()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.BlockCountries("KP");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.True(options!.Value.Enabled);
    }

    [Fact]
    public void BlockCountries_SingleCountry()
    {
        // Arrange
        var services = new ServiceCollection();
        services.AddLogging();

        // Act
        services.BlockCountries("KP");

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<GeoRoutingOptions>>();
        Assert.Single(options!.Value.BlockedCountries!);
    }

    [Fact]
    public void BlockCountries_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();

        // Act
        var result = services.BlockCountries("KP", "IR");

        // Assert
        Assert.Same(services, result);
    }

    #endregion
}