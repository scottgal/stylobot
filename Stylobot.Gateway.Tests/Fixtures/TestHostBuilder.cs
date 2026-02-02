using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Middleware;

namespace Stylobot.Gateway.Tests.Fixtures;

/// <summary>
/// Fluent builder for creating test hosts with common configurations.
/// Reduces duplication across middleware and integration tests.
/// </summary>
public class TestHostBuilder
{
    private string? _adminSecret;
    private string _adminBasePath = "/admin";
    private Action<IEndpointRouteBuilder>? _endpointConfig;
    private Action<IServiceCollection>? _serviceConfig;
    private bool _useAdminMiddleware = true;

    public static TestHostBuilder Create() => new();

    public TestHostBuilder WithAdminSecret(string? secret)
    {
        _adminSecret = secret;
        return this;
    }

    public TestHostBuilder WithAdminPath(string path)
    {
        _adminBasePath = path;
        return this;
    }

    public TestHostBuilder WithEndpoints(Action<IEndpointRouteBuilder> config)
    {
        _endpointConfig = config;
        return this;
    }

    public TestHostBuilder WithServices(Action<IServiceCollection> config)
    {
        _serviceConfig = config;
        return this;
    }

    public TestHostBuilder WithoutAdminMiddleware()
    {
        _useAdminMiddleware = false;
        return this;
    }

    public IHost Build()
    {
        var builder = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(webBuilder =>
            {
                webBuilder.UseTestServer();
                webBuilder.ConfigureServices(services =>
                {
                    services.Configure<GatewayOptions>(opts =>
                    {
                        opts.AdminSecret = _adminSecret;
                        opts.AdminBasePath = _adminBasePath;
                    });
                    _serviceConfig?.Invoke(services);
                });
                webBuilder.Configure(app =>
                {
                    if (_useAdminMiddleware)
                        app.UseAdminSecretMiddleware();

                    app.UseRouting();

                    if (_endpointConfig != null)
                    {
                        app.UseEndpoints(_endpointConfig);
                    }
                });
            });

        return builder.Build();
    }

    public async Task<(IHost Host, HttpClient Client)> BuildAndStartAsync()
    {
        var host = Build();
        await host.StartAsync();
        return (host, host.GetTestClient());
    }
}

/// <summary>
/// Extension methods for test requests.
/// </summary>
public static class TestRequestExtensions
{
    /// <summary>
    /// Creates a request with the admin secret header.
    /// </summary>
    public static HttpRequestMessage WithAdminSecret(this HttpRequestMessage request, string secret)
    {
        request.Headers.Add("X-Admin-Secret", secret);
        return request;
    }

    /// <summary>
    /// Creates a GET request with optional admin secret.
    /// </summary>
    public static HttpRequestMessage CreateGet(string path, string? adminSecret = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (adminSecret != null)
            request.WithAdminSecret(adminSecret);
        return request;
    }
}

/// <summary>
/// Utility for finding available ports.
/// </summary>
public static class PortUtility
{
    public static int GetAvailablePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
