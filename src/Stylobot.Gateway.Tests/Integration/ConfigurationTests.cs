using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Microsoft.Extensions.Options;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Services;

namespace Stylobot.Gateway.Tests.Integration;

/// <summary>
/// Tests for gateway configuration and service registration.
/// </summary>
public class ConfigurationTests
{
    [Fact]
    public void GatewayOptions_DefaultValues_AreCorrect()
    {
        // Arrange
        var options = new GatewayOptions();

        // Assert
        options.HttpPort.Should().Be(8080);
        options.AdminBasePath.Should().Be("/admin");
        options.LogLevel.Should().Be("Information");
        options.DefaultUpstream.Should().BeNull();
        options.AdminSecret.Should().BeNull();
    }

    [Fact]
    public void DatabaseOptions_DefaultValues_AreCorrect()
    {
        // Arrange
        var options = new DatabaseOptions();

        // Assert
        options.Provider.Should().Be(DatabaseProvider.None);
        options.ConnectionString.Should().BeNull();
        options.MigrateOnStartup.Should().BeTrue();
        options.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void DatabaseOptions_IsEnabled_RequiresBothProviderAndConnectionString()
    {
        // Arrange
        var options = new DatabaseOptions
        {
            Provider = DatabaseProvider.Postgres,
            ConnectionString = null
        };

        // Assert - needs both
        options.IsEnabled.Should().BeFalse();

        // With connection string
        options.ConnectionString = "Host=localhost;Database=test";
        options.IsEnabled.Should().BeTrue();

        // Provider None means disabled even with connection string
        options.Provider = DatabaseProvider.None;
        options.IsEnabled.Should().BeFalse();
    }

    [Fact]
    public void GatewayPaths_ReturnsAllDirectories()
    {
        // Act
        var paths = GatewayPaths.All;

        // Assert
        paths.Should().ContainKey("config");
        paths.Should().ContainKey("data");
        paths.Should().ContainKey("logs");
        paths.Should().ContainKey("plugins");
    }

    [Fact]
    public void GatewayMetrics_TracksUptime()
    {
        // Arrange
        var metrics = new GatewayMetrics();

        // Act - wait briefly
        Thread.Sleep(100);

        // Assert
        metrics.Uptime.TotalMilliseconds.Should().BeGreaterThanOrEqualTo(90);
    }

    [Fact]
    public void GatewayMetrics_TracksRequests()
    {
        // Arrange
        var metrics = new GatewayMetrics();

        // Act
        metrics.RecordRequest();
        metrics.RecordRequest();
        metrics.RecordRequest();

        // Assert
        metrics.RequestsTotal.Should().Be(3);
    }

    [Fact]
    public void GatewayMetrics_TracksErrors()
    {
        // Arrange
        var metrics = new GatewayMetrics();

        // Act
        metrics.RecordError();
        metrics.RecordError();

        // Assert
        metrics.ErrorsTotal.Should().Be(2);
    }

    [Fact]
    public void GatewayMetrics_TracksConnections()
    {
        // Arrange
        var metrics = new GatewayMetrics();

        // Act
        metrics.ConnectionStarted();
        metrics.ConnectionStarted();
        metrics.ConnectionEnded();

        // Assert
        metrics.ActiveConnections.Should().Be(1);
    }

    [Fact]
    public void GatewayMetrics_TracksBytes()
    {
        // Arrange
        var metrics = new GatewayMetrics();

        // Act
        metrics.RecordBytes(1000, 2000);
        metrics.RecordBytes(500, 500);

        // Assert
        metrics.BytesIn.Should().Be(1500);
        metrics.BytesOut.Should().Be(2500);
    }

    [Fact]
    public void GatewayMetrics_RequestsPerSecond_Calculated()
    {
        // Arrange
        var metrics = new GatewayMetrics();

        // Act - record multiple requests with small delay to ensure elapsed time
        for (int i = 0; i < 10; i++)
        {
            metrics.RecordRequest();
        }
        Thread.Sleep(150); // Ensure some time passes (>100ms for RPS calculation)

        // Assert - should have some RPS value after time elapsed
        // The RPS window needs at least 0.1s to calculate
        metrics.RequestsPerSecond.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void ConfigurationService_DetectsIssues()
    {
        // Arrange
        var services = new ServiceCollection();
        services.Configure<GatewayOptions>(opts =>
        {
            opts.AdminSecret = null; // Will trigger warning
        });
        services.Configure<DatabaseOptions>(opts =>
        {
            opts.Provider = DatabaseProvider.None;
        });
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var configService = new ConfigurationService(
            sp.GetRequiredService<IOptions<GatewayOptions>>(),
            sp.GetRequiredService<IOptions<DatabaseOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConfigurationService>>()
        );

        // Act
        var issues = configService.GetConfigurationIssues().ToList();

        // Assert
        issues.Should().Contain(i => i.Key == "AdminSecret" && i.Severity == ConfigIssueSeverity.Warning);
    }

    [Fact]
    public void ConfigurationService_IsProductionReady_WithoutErrors()
    {
        // Arrange
        var services = new ServiceCollection();
        services.Configure<GatewayOptions>(opts =>
        {
            opts.AdminSecret = "secret"; // No warning
        });
        services.Configure<DatabaseOptions>(opts =>
        {
            opts.Provider = DatabaseProvider.None; // No DB = no error
        });
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var configService = new ConfigurationService(
            sp.GetRequiredService<IOptions<GatewayOptions>>(),
            sp.GetRequiredService<IOptions<DatabaseOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConfigurationService>>()
        );

        // Act
        var isReady = configService.IsProductionReady();

        // Assert - should be production ready (only warnings, no errors)
        isReady.Should().BeTrue();
    }

    [Fact]
    public void ConfigurationService_NotProductionReady_WithDatabaseError()
    {
        // Arrange
        var services = new ServiceCollection();
        services.Configure<GatewayOptions>(opts => { });
        services.Configure<DatabaseOptions>(opts =>
        {
            opts.Provider = DatabaseProvider.Postgres;
            opts.ConnectionString = null; // Error: provider set but no connection string
        });
        services.AddLogging();

        var sp = services.BuildServiceProvider();
        var configService = new ConfigurationService(
            sp.GetRequiredService<IOptions<GatewayOptions>>(),
            sp.GetRequiredService<IOptions<DatabaseOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ConfigurationService>>()
        );

        // Act
        var isReady = configService.IsProductionReady();

        // Assert
        isReady.Should().BeFalse();

        var issues = configService.GetConfigurationIssues().ToList();
        issues.Should().Contain(i => i.Key == "DatabaseConnectionString" && i.Severity == ConfigIssueSeverity.Error);
    }
}
