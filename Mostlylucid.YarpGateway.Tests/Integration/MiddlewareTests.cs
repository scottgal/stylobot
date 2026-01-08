using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Hosting;
using Mostlylucid.YarpGateway.Tests.Fixtures;
using Xunit;

namespace Mostlylucid.YarpGateway.Tests.Integration;

/// <summary>
/// Tests for the AdminSecretMiddleware - consolidated test class.
/// Uses Theory/InlineData to reduce duplication while testing multiple scenarios.
/// </summary>
public class AdminSecretMiddlewareTests
{
    private const string TestSecret = "test-admin-secret";

    private static async Task<(IHost Host, HttpClient Client)> CreateTestHostAsync(
        string? adminSecret, string adminPath = "/admin")
    {
        return await TestHostBuilder.Create()
            .WithAdminSecret(adminSecret)
            .WithAdminPath(adminPath)
            .WithEndpoints(endpoints =>
            {
                endpoints.MapGet("/admin/test", () => "admin content");
                endpoints.MapGet("/management/test", () => "management content");
                endpoints.MapGet("/public/test", () => "public content");
            })
            .BuildAndStartAsync();
    }

    #region Secret Validation Tests

    [Fact]
    public async Task AdminPath_WithoutSecret_ReturnsUnauthorized()
    {
        var (host, client) = await CreateTestHostAsync(TestSecret);
        try
        {
            var response = await client.GetAsync("/admin/test");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await CleanupAsync(host, client);
        }
    }

    [Fact]
    public async Task AdminPath_WithCorrectSecret_ReturnsOk()
    {
        var (host, client) = await CreateTestHostAsync(TestSecret);
        try
        {
            var request = TestRequestExtensions.CreateGet("/admin/test", TestSecret);
            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("admin content");
        }
        finally
        {
            await CleanupAsync(host, client);
        }
    }

    [Theory]
    [InlineData("wrong-secret")]
    [InlineData("")]
    [InlineData("TEST-ADMIN-SECRET")] // Case sensitive
    public async Task AdminPath_WithInvalidSecret_ReturnsUnauthorized(string invalidSecret)
    {
        var (host, client) = await CreateTestHostAsync(TestSecret);
        try
        {
            var request = TestRequestExtensions.CreateGet("/admin/test", invalidSecret);
            var response = await client.SendAsync(request);

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await CleanupAsync(host, client);
        }
    }

    #endregion

    #region No Secret Configured Tests

    [Fact]
    public async Task AdminPath_WithNoSecretConfigured_AllowsAccess()
    {
        var (host, client) = await CreateTestHostAsync(adminSecret: null);
        try
        {
            var response = await client.GetAsync("/admin/test");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("admin content");
        }
        finally
        {
            await CleanupAsync(host, client);
        }
    }

    #endregion

    #region Path Matching Tests

    [Fact]
    public async Task NonAdminPath_WithoutSecret_ReturnsOk()
    {
        var (host, client) = await CreateTestHostAsync(TestSecret);
        try
        {
            var response = await client.GetAsync("/public/test");

            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await response.Content.ReadAsStringAsync()).Should().Be("public content");
        }
        finally
        {
            await CleanupAsync(host, client);
        }
    }

    [Fact]
    public async Task CustomAdminPath_RequiresSecret()
    {
        var (host, client) = await CreateTestHostAsync("secret", "/management");
        try
        {
            var response = await client.GetAsync("/management/test");
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        }
        finally
        {
            await CleanupAsync(host, client);
        }
    }

    [Fact]
    public async Task CustomAdminPath_OldAdminPathIsPublic()
    {
        var (host, client) = await CreateTestHostAsync("secret", "/management");
        try
        {
            // /admin is NOT the configured admin path, so it's public
            var response = await client.GetAsync("/admin/test");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
        finally
        {
            await CleanupAsync(host, client);
        }
    }

    #endregion

    #region Response Format Tests

    [Fact]
    public async Task UnauthorizedResponse_HasCorrectFormat()
    {
        var (host, client) = await CreateTestHostAsync(TestSecret);
        try
        {
            var response = await client.GetAsync("/admin/test");

            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
            response.Content.Headers.ContentType?.MediaType.Should().Be("application/json");

            var content = await response.Content.ReadAsStringAsync();
            content.Should().Contain("unauthorized");
            content.Should().Contain("X-Admin-Secret");
        }
        finally
        {
            await CleanupAsync(host, client);
        }
    }

    #endregion

    private static async Task CleanupAsync(IHost host, HttpClient client)
    {
        client.Dispose();
        await host.StopAsync();
        host.Dispose();
    }
}
