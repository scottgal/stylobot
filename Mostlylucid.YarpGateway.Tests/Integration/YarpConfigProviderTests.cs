using FluentAssertions;
using Mostlylucid.YarpGateway.Configuration;
using Xunit;

namespace Mostlylucid.YarpGateway.Tests.Integration;

/// <summary>
/// Tests for YARP configuration providers.
/// </summary>
public class YarpConfigProviderTests
{
    [Fact]
    public void DefaultUpstreamConfigProvider_CreatesCatchAllRoute()
    {
        // Arrange
        var upstreamUrl = "http://backend:3000";
        var provider = new DefaultUpstreamConfigProvider(upstreamUrl);

        // Act
        var config = provider.GetConfig();

        // Assert
        config.Routes.Should().HaveCount(1);
        config.Routes[0].RouteId.Should().Be("default-catch-all");
        config.Routes[0].ClusterId.Should().Be("default-upstream");
        config.Routes[0].Match.Path.Should().Be("/{**catch-all}");
    }

    [Fact]
    public void DefaultUpstreamConfigProvider_CreatesClusterWithDestination()
    {
        // Arrange
        var upstreamUrl = "http://backend:3000";
        var provider = new DefaultUpstreamConfigProvider(upstreamUrl);

        // Act
        var config = provider.GetConfig();

        // Assert
        config.Clusters.Should().HaveCount(1);
        config.Clusters[0].ClusterId.Should().Be("default-upstream");
        config.Clusters[0].Destinations.Should().ContainKey("primary");
        config.Clusters[0].Destinations!["primary"].Address.Should().Be(upstreamUrl);
    }

    [Fact]
    public void DefaultUpstreamConfigProvider_HasNoChangeToken()
    {
        // Arrange
        var provider = new DefaultUpstreamConfigProvider("http://backend:3000");

        // Act
        var config = provider.GetConfig();

        // Assert - change token should not signal changes (static config)
        config.ChangeToken.HasChanged.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://localhost:3000")]
    [InlineData("https://api.example.com")]
    [InlineData("http://192.168.1.100:8080")]
    public void DefaultUpstreamConfigProvider_AcceptsVariousUrls(string upstreamUrl)
    {
        // Arrange & Act
        var provider = new DefaultUpstreamConfigProvider(upstreamUrl);
        var config = provider.GetConfig();

        // Assert
        config.Clusters[0].Destinations!["primary"].Address.Should().Be(upstreamUrl);
    }

    [Fact]
    public void EmptyConfigProvider_ReturnsNoRoutes()
    {
        // Arrange
        var provider = new EmptyConfigProvider();

        // Act
        var config = provider.GetConfig();

        // Assert
        config.Routes.Should().BeEmpty();
        config.Clusters.Should().BeEmpty();
    }

    [Fact]
    public void EmptyConfigProvider_HasNoChangeToken()
    {
        // Arrange
        var provider = new EmptyConfigProvider();

        // Act
        var config = provider.GetConfig();

        // Assert
        config.ChangeToken.HasChanged.Should().BeFalse();
    }

    [Fact]
    public void ConfigProvider_GetConfig_ReturnsConsistentResults()
    {
        // Arrange
        var provider = new DefaultUpstreamConfigProvider("http://backend:3000");

        // Act
        var config1 = provider.GetConfig();
        var config2 = provider.GetConfig();

        // Assert - should return same config instance (not create new each time)
        config1.Routes.Should().BeEquivalentTo(config2.Routes);
        config1.Clusters.Should().BeEquivalentTo(config2.Clusters);
    }
}
