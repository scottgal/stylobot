using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Test.Extensions;

/// <summary>
///     Tests for BotDetection ServiceCollectionExtensions
/// </summary>
public class ServiceCollectionExtensionsTests
{
    /// <summary>
    ///     Creates an empty IConfiguration for tests.
    ///     Required because AddBotDetection() uses BindConfiguration which needs IConfiguration.
    /// </summary>
    private static IConfiguration CreateEmptyConfiguration()
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection()
            .Build();
    }

    /// <summary>
    ///     Adds standard test dependencies (logging, cache, configuration)
    /// </summary>
    private static void AddTestDependencies(IServiceCollection services)
    {
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(CreateEmptyConfiguration());
    }

    #region Multiple Registration Tests

    [Fact]
    public void AddBotDetection_CalledMultipleTimes_DoesNotThrow()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act - Should not throw
        services.AddBotDetection();
        var exception = Record.Exception(() => services.AddBotDetection());

        // Assert
        Assert.Null(exception);
    }

    #endregion

    #region AddBotDetection Tests

    [Fact]
    public void AddBotDetection_RegistersServices()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider);
    }

    [Fact]
    public void AddBotDetection_WithOptions_RegistersOptions()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection(options =>
        {
            options.BotThreshold = 0.8;
            options.EnableLlmDetection = true;
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<BotDetectionOptions>>();
        Assert.NotNull(options);
        Assert.Equal(0.8, options.Value.BotThreshold);
        Assert.True(options.Value.EnableLlmDetection);
    }

    [Fact]
    public void AddBotDetection_RegistersBotDetectionService()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var service = provider.GetService<IBotDetectionService>();
        Assert.NotNull(service);
    }

    [Fact]
    public void AddBotDetection_RegistersDetectors()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var detectors = provider.GetServices<IDetector>();
        Assert.NotEmpty(detectors);
    }

    [Fact]
    public void AddBotDetection_RegistersUserAgentDetector()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var detectors = provider.GetServices<IDetector>();
        Assert.Contains(detectors, d => d is UserAgentDetector);
    }

    [Fact]
    public void AddBotDetection_RegistersHeaderDetector()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var detectors = provider.GetServices<IDetector>();
        Assert.Contains(detectors, d => d is HeaderDetector);
    }

    [Fact]
    public void AddBotDetection_RegistersIpDetector()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var detectors = provider.GetServices<IDetector>();
        Assert.Contains(detectors, d => d is IpDetector);
    }

    [Fact]
    public void AddBotDetection_RegistersBehavioralDetector()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var detectors = provider.GetServices<IDetector>();
        Assert.Contains(detectors, d => d is BehavioralDetector);
    }

    #endregion

    #region Options Configuration Tests

    [Fact]
    public void AddBotDetection_NullOptions_UsesDefaults()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        services.AddBotDetection();

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<BotDetectionOptions>>();
        Assert.NotNull(options);
        Assert.Equal(0.7, options.Value.BotThreshold); // Default value
    }

    [Fact]
    public void AddBotDetection_CustomOptions_Preserved()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);
        var customPatterns = new List<string> { "CustomBot1", "CustomBot2" };

        // Act
        services.AddBotDetection(options =>
        {
            options.WhitelistedBotPatterns = customPatterns;
            options.MaxRequestsPerMinute = 120;
            options.CacheDurationSeconds = 600;
        });

        // Assert
        var provider = services.BuildServiceProvider();
        var options = provider.GetService<IOptions<BotDetectionOptions>>();
        Assert.Equal(customPatterns, options!.Value.WhitelistedBotPatterns);
        Assert.Equal(120, options.Value.MaxRequestsPerMinute);
        Assert.Equal(600, options.Value.CacheDurationSeconds);
    }

    #endregion

    #region Return Value Tests

    [Fact]
    public void AddBotDetection_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        var result = services.AddBotDetection();

        // Assert
        Assert.Same(services, result);
    }

    [Fact]
    public void AddBotDetection_WithOptions_ReturnsServiceCollection()
    {
        // Arrange
        var services = new ServiceCollection();
        AddTestDependencies(services);

        // Act
        var result = services.AddBotDetection(options => { });

        // Assert
        Assert.Same(services, result);
    }

    #endregion
}