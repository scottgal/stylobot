using System.Security.Claims;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.Test.UI;

public sealed class StyloBotDashboardHubAuthorizationTests
{
    [Fact]
    public async Task Production_without_auth_configuration_is_denied()
    {
        var allowed = await AuthorizeAsync(new StyloBotDashboardOptions(), "Production");
        Assert.False(allowed);
    }

    [Fact]
    public async Task Explicit_unauthenticated_access_is_allowed()
    {
        var allowed = await AuthorizeAsync(
            new StyloBotDashboardOptions { AllowUnauthenticatedAccess = true }, "Production");
        Assert.True(allowed);
    }

    [Fact]
    public async Task RequireAuthentication_denies_anonymous_connection()
    {
        var allowed = await AuthorizeAsync(
            new StyloBotDashboardOptions { RequireAuthentication = true }, "Production");
        Assert.False(allowed);
    }

    [Fact]
    public async Task Configured_authorization_policy_allows_authenticated_connection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder().AddPolicy("dashboard", policy => policy.RequireAuthenticatedUser());
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test"));

        var allowed = await StyloBotDashboardHub.IsAuthorizedAsync(
            context,
            new StyloBotDashboardOptions { RequireAuthorizationPolicy = "dashboard" },
            new TestEnvironment("Production"));

        Assert.True(allowed);
    }

    private static Task<bool> AuthorizeAsync(StyloBotDashboardOptions options, string environmentName) =>
        StyloBotDashboardHub.IsAuthorizedAsync(
            new DefaultHttpContext(), options, new TestEnvironment(environmentName));

    private sealed class TestEnvironment(string environmentName) : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;
        public string ApplicationName { get; set; } = "test";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
