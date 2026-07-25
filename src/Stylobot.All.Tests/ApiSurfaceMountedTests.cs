using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Stylobot.All.Tests;

/// <summary>
/// Regression coverage for a confirmed bug: Program.cs called AddStyloBotApi() (registers
/// the DI services so OpenAPI/detect/etc config binds) but never called MapStyloBotApi()
/// (the extension that actually maps the /api/v1/* route group and /api/v1/openapi.json).
/// Hitting the REST API surface on a running Stylobot.All host silently 404'd despite the
/// API being "enabled" in config. This test boots the real Stylobot.All host (via
/// WebApplicationFactory&lt;Program&gt;, the actual Program.cs entry point) and asserts the
/// route is actually mounted.
/// </summary>
public class ApiSurfaceMountedTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public ApiSurfaceMountedTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                // Keep the test host self-contained: don't depend on ReverseProxy
                // clusters/routes resolving to anything, and point the SQLite-backed
                // detection store at the test's isolated working directory (the
                // default ResolveDataDirectory() falls back to CWD, which is fine
                // under `dotnet test` but pin it explicitly for determinism).
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["StyloBot:Api:EnableOpenApi"] = "true",
                });
            });
        });
    }

    [Fact]
    public async Task OpenApiJson_IsMounted_ReturnsSuccess()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/openapi.json");

        // Before the fix (MapStyloBotApi() never called): 404 NotFound, because
        // AddStyloBotApi() only registers DI services -- it never adds the
        // /api/v1/* route group to the endpoint route builder.
        // After the fix: the openapi document is served.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task MeEndpoint_IsMounted_DoesNotReturn404()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/v1/me");

        // The /me endpoint requires API-key auth, so an unauthenticated request is
        // expected to come back 401/403 once the route is mounted. What it must
        // NOT be is 404 -- that would mean the route group was never mapped at all,
        // which is exactly the bug under test.
        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);
    }
}
