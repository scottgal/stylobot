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
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // AddBotDetection now fails fast when DatabasePath is null (a null
                // path used to fall back SILENTLY to an unbounded in-memory SQLite DB).
                // These ephemeral option-binding tests never persist, so they opt into
                // in-memory explicitly the same way AddBotDetectionInMemory does:
                // DatabasePath = empty. Empty passes validation; only null is rejected.
                ["BotDetection:DatabasePath"] = string.Empty,
            })
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

    #region DatabasePath fail-loud contract

    /// <summary>
    ///     Regression guard for the silent-in-memory drift: a null DatabasePath must
    ///     FAIL LOUD (StyloBot persists to SQLite; a null path used to fall back
    ///     silently to an unbounded in-memory DB that OOMs under load). Resolving the
    ///     options must throw with actionable guidance, not quietly succeed.
    /// </summary>
    [Fact]
    public void AddBotDetection_NullDatabasePath_ThrowsWithGuidance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        // Deliberately NO DatabasePath key -> binds to null.
        services.AddSingleton<IConfiguration>(
            new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddBotDetection();

        var provider = services.BuildServiceProvider();

        var ex = Assert.Throws<OptionsValidationException>(
            () => _ = provider.GetRequiredService<IOptions<BotDetectionOptions>>().Value);
        Assert.Contains("DatabasePath", ex.Message);
        Assert.Contains("AddBotDetectionInMemory", ex.Message);
    }

    /// <summary>
    ///     The explicit ephemeral opt-in (empty DatabasePath, as AddBotDetectionInMemory
    ///     sets) is the ONLY allowed in-memory path and must NOT throw.
    /// </summary>
    [Fact]
    public void AddBotDetection_EmptyDatabasePath_IsAllowed()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["BotDetection:DatabasePath"] = string.Empty,
            })
            .Build());
        services.AddBotDetection();

        var provider = services.BuildServiceProvider();

        var options = provider.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
        Assert.Equal(string.Empty, options.DatabasePath);
    }

    #endregion

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
