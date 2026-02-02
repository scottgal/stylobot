using System.Net;
using Microsoft.AspNetCore.Builder;
using Xunit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Endpoints;
using Stylobot.Gateway.Middleware;
using Stylobot.Gateway.Services;

namespace Stylobot.Gateway.Tests.Fixtures;

/// <summary>
/// Test fixture that sets up both the gateway and a mock upstream server.
/// Uses YARP best practices for integration testing.
/// </summary>
public class GatewayTestFixture : IAsyncLifetime
{
    private IHost? _upstreamHost;
    private IHost? _gatewayHost;

    public HttpClient GatewayClient { get; private set; } = null!;
    public HttpClient UpstreamClient { get; private set; } = null!;
    public int UpstreamPort { get; private set; }

    /// <summary>
    /// Recorded requests to the upstream server for verification.
    /// </summary>
    public List<RecordedRequest> RecordedRequests { get; } = new();

    /// <summary>
    /// Configure upstream response behavior.
    /// </summary>
    public Func<HttpContext, Task<bool>>? UpstreamHandler { get; set; }

    public async Task InitializeAsync()
    {
        // Find available port for upstream
        UpstreamPort = GetAvailablePort();

        // Start mock upstream server
        _upstreamHost = await CreateUpstreamHost();
        await _upstreamHost.StartAsync();
        UpstreamClient = new HttpClient { BaseAddress = new Uri($"http://localhost:{UpstreamPort}") };

        // Start gateway with upstream configured
        _gatewayHost = await CreateGatewayHost();
        await _gatewayHost.StartAsync();
        GatewayClient = _gatewayHost.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        GatewayClient?.Dispose();
        UpstreamClient?.Dispose();

        if (_gatewayHost != null)
            await _gatewayHost.StopAsync();

        if (_upstreamHost != null)
            await _upstreamHost.StopAsync();

        _gatewayHost?.Dispose();
        _upstreamHost?.Dispose();
    }

    private Task<IHost> CreateUpstreamHost()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseUrls($"http://localhost:{UpstreamPort}");
                webBuilder.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapFallback(async context =>
                        {
                            // Record the request
                            RecordedRequests.Add(new RecordedRequest
                            {
                                Method = context.Request.Method,
                                Path = context.Request.Path.ToString(),
                                QueryString = context.Request.QueryString.ToString(),
                                Headers = context.Request.Headers
                                    .ToDictionary(h => h.Key, h => h.Value.ToString())
                            });

                            // Use custom handler if provided
                            if (UpstreamHandler != null)
                            {
                                var handled = await UpstreamHandler(context);
                                if (handled) return;
                            }

                            // Default response based on path
                            if (context.Request.Path.StartsWithSegments("/api/echo"))
                            {
                                context.Response.ContentType = "application/json";
                                await context.Response.WriteAsJsonAsync(new
                                {
                                    method = context.Request.Method,
                                    path = context.Request.Path.ToString(),
                                    query = context.Request.QueryString.ToString(),
                                    headers = context.Request.Headers
                                        .Where(h => !h.Key.StartsWith("X-Forwarded"))
                                        .ToDictionary(h => h.Key, h => h.Value.ToString())
                                });
                            }
                            else if (context.Request.Path.StartsWithSegments("/api/slow"))
                            {
                                await Task.Delay(500);
                                await context.Response.WriteAsync("slow response");
                            }
                            else if (context.Request.Path.StartsWithSegments("/api/error"))
                            {
                                context.Response.StatusCode = 500;
                                await context.Response.WriteAsync("internal error");
                            }
                            else if (context.Request.Path == "/health" || context.Request.Path == "/upstream-health")
                            {
                                await context.Response.WriteAsJsonAsync(new { status = "healthy" });
                            }
                            else
                            {
                                context.Response.ContentType = "text/plain";
                                await context.Response.WriteAsync($"upstream received: {context.Request.Path}");
                            }
                        });
                    });
                });
            });

        return Task.FromResult(builder.Build());
    }

    private Task<IHost> CreateGatewayHost()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices((context, services) =>
                {
                    // Configure gateway options
                    services.Configure<GatewayOptions>(opts =>
                    {
                        opts.HttpPort = 8080;
                        opts.AdminBasePath = "/admin";
                        opts.AdminSecret = "test-secret";
                        opts.DefaultUpstream = $"http://localhost:{UpstreamPort}";
                    });

                    services.Configure<DatabaseOptions>(opts =>
                    {
                        opts.Provider = DatabaseProvider.None;
                    });

                    // Add YARP with default upstream
                    var proxyBuilder = services.AddReverseProxy();
                    services.AddSingleton<Yarp.ReverseProxy.Configuration.IProxyConfigProvider>(sp =>
                        new DefaultUpstreamConfigProvider($"http://localhost:{UpstreamPort}"));

                    services.AddGatewayServices();
                    services.AddHealthChecks();
                });

                webBuilder.Configure(app =>
                {
                    app.UseAdminSecretMiddleware();
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapAdminEndpoints();
                        endpoints.MapReverseProxy();
                    });
                });
            });

        return Task.FromResult(builder.Build());
    }

    private static int GetAvailablePort() => PortUtility.GetAvailablePort();

    /// <summary>
    /// Clear recorded requests between tests.
    /// </summary>
    public void ClearRecordedRequests() => RecordedRequests.Clear();
}

/// <summary>
/// Represents a request recorded by the upstream server.
/// </summary>
public class RecordedRequest
{
    public required string Method { get; init; }
    public required string Path { get; init; }
    public string QueryString { get; init; } = "";
    public Dictionary<string, string> Headers { get; init; } = new();
}
