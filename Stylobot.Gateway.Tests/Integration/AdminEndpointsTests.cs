using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Stylobot.Gateway.Tests.Fixtures;
using Xunit;

namespace Stylobot.Gateway.Tests.Integration;

/// <summary>
/// Tests for the Admin API endpoints.
/// </summary>
public class AdminEndpointsTests : IClassFixture<GatewayTestFixture>
{
    private const string ValidSecret = "test-secret";
    private readonly GatewayTestFixture _fixture;

    public AdminEndpointsTests(GatewayTestFixture fixture)
    {
        _fixture = fixture;
    }

    private Task<HttpResponseMessage> GetAdminAsync(string path, string? secret = null)
    {
        var request = TestRequestExtensions.CreateGet(path, secret);
        return _fixture.GatewayClient.SendAsync(request);
    }

    [Fact]
    public async Task Health_WithoutSecret_ReturnsUnauthorized()
    {
        var response = await GetAdminAsync("/admin/health");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Health_WithValidSecret_ReturnsOk()
    {
        var response = await GetAdminAsync("/admin/health", ValidSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<HealthResponse>();
        content.Should().NotBeNull();
        content!.Status.Should().Be("ok");
        content.RoutesConfigured.Should().BeGreaterThanOrEqualTo(0);
        content.UptimeSeconds.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Health_WithInvalidSecret_ReturnsUnauthorized()
    {
        var response = await GetAdminAsync("/admin/health", "wrong-secret");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task EffectiveConfig_WithValidSecret_ReturnsConfiguration()
    {
        var response = await GetAdminAsync("/admin/config/effective", ValidSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<EffectiveConfigResponse>();
        content.Should().NotBeNull();
        content!.Gateway.Should().NotBeNull();
        content.Database.Should().NotBeNull();
        content.Yarp.Should().NotBeNull();
    }

    [Fact]
    public async Task ConfigSources_WithValidSecret_ReturnsSources()
    {
        var response = await GetAdminAsync("/admin/config/sources", ValidSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("sources");
        json.Should().Contain("built-in");
    }

    [Fact]
    public async Task Routes_WithValidSecret_ReturnsRoutes()
    {
        var response = await GetAdminAsync("/admin/routes", ValidSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<RoutesResponse>();
        content.Should().NotBeNull();
        content!.Count.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Clusters_WithValidSecret_ReturnsClusters()
    {
        var response = await GetAdminAsync("/admin/clusters", ValidSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<ClustersResponse>();
        content.Should().NotBeNull();
        content!.Count.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task Metrics_WithValidSecret_ReturnsMetrics()
    {
        var response = await GetAdminAsync("/admin/metrics", ValidSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await response.Content.ReadFromJsonAsync<MetricsResponse>();
        content.Should().NotBeNull();
        content!.UptimeSeconds.Should().BeGreaterThanOrEqualTo(0);
        content.RequestsTotal.Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task FileSystems_WithValidSecret_ReturnsDirectories()
    {
        var response = await GetAdminAsync("/admin/fs", ValidSecret);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("directories");
    }

    // Response DTOs for deserialization
    private record HealthResponse(string Status, long UptimeSeconds, int RoutesConfigured, int ClustersConfigured, string Mode, string Db);
    private record EffectiveConfigResponse(GatewayConfig Gateway, DatabaseConfig Database, YarpConfig Yarp);
    private record GatewayConfig(int HttpPort, string AdminBasePath, bool HasAdminSecret, string? DefaultUpstream, string LogLevel);
    private record DatabaseConfig(string Provider, bool Enabled, bool MigrateOnStartup);
    private record YarpConfig(int RouteCount, int ClusterCount);
    private record RoutesResponse(int Count);
    private record ClustersResponse(int Count);
    private record MetricsResponse(long UptimeSeconds, long RequestsTotal, double RequestsPerSecond, long ErrorsTotal, long ActiveConnections, long BytesIn, long BytesOut);
}
