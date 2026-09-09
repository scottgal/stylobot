using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Api;

/// <summary>
///     C6 (e): <c>GET /api/v1/config/sections</c> joins the manifests endpoints on the API surface,
///     so the admin console can read both halves from one host with one key. It must carry the
///     SAME auth shape as the existing manifests group — API-key auth, no third posture.
/// </summary>
public sealed class ConfigSectionsEndpointTests : IAsyncDisposable
{
    private const string ValidKey = "test-config-sections-key";
    private WebApplication? _app;

    private async Task<WebApplication> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddOptions<BotDetectionOptions>().Configure(o =>
            o.ApiKeys["test"] = new ApiKeyConfig { Key = ValidKey, Name = "test", Enabled = true });
        builder.Services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
                ApiKeyAuthenticationHandler.SchemeName, _ => { });
        // The endpoints require a POLICY named after the scheme (RequireAuthorization(SchemeName)).
        builder.Services.AddAuthorization(o => o.AddPolicy(
            ApiKeyAuthenticationHandler.SchemeName,
            p => p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser()));

        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapConfigEndpoints();
        await app.StartAsync();
        return app;
    }

    [Fact]
    public async Task Sections_returns_the_editor_sections_with_a_valid_key()
    {
        _app = await StartAsync();
        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, ValidKey);

        var response = await client.GetAsync("/api/v1/config/sections");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        var data = JsonDocument.Parse(body).RootElement.GetProperty("data");
        Assert.True(data.GetArrayLength() > 0, "the sections list must not be empty");
        Assert.False(string.IsNullOrWhiteSpace(data[0].GetProperty("id").GetString()));
    }

    [Fact]
    public async Task Sections_requires_the_same_api_key_as_the_manifests_endpoints()
    {
        _app = await StartAsync();
        var client = _app.GetTestClient();

        var response = await client.GetAsync("/api/v1/config/sections");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
