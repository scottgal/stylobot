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

    /// <summary>
    ///     REGRESSION GUARD for the live SignalR outage (2026-08-09): the gateway aborted
    ///     EVERY hub connection with "dashboard auth failed", so the dashboard rendered once
    ///     and then froze — Signal Shingle's whole update path is SignalR beacon → HTMX OOB
    ///     swap, so a dead hub means no widget ever updates.
    ///
    ///     <para>
    ///         Cause: <see cref="StyloBotDashboardMiddleware"/> evaluates FIVE auth paths; the
    ///         hub only had FOUR. The missing one was <see cref="DashboardAuthMode.Login"/> —
    ///         the FOSS config-credential mode (<c>stylobot dashboard hash-password</c>) that
    ///         every real deployment uses. The page authenticated fine and the hub, having no
    ///         branch for that mode, fell through to AllowUnauthenticatedAccess (false off-dev)
    ///         then IsDevelopment() (false on a server), and refused the same client.
    ///     </para>
    ///
    ///     <para>
    ///         The pre-existing tests in this file covered RequireAuthentication,
    ///         RequireAuthorizationPolicy, AllowUnauthenticatedAccess and the unconfigured
    ///         case — every mode EXCEPT the one real deployments run on. That is why a hub
    ///         with auth tests still shipped unable to authenticate anybody.
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Login_mode_denies_an_anonymous_connection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };

        var allowed = await StyloBotDashboardHub.IsAuthorizedAsync(
            context, LoginModeOptions(), new TestEnvironment("Production"));

        Assert.False(allowed);
    }

    /// <summary>
    ///     The half that was broken: in Login mode an AUTHENTICATED client must be allowed.
    ///     Before the fix this returned false regardless of credentials, because the branch
    ///     did not exist — which is exactly what aborted every connection in production.
    /// </summary>
    [Fact]
    public async Task Login_mode_allows_an_authenticated_connection()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAuthorizationBuilder();
        using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };
        context.User = new ClaimsPrincipal(
            new ClaimsIdentity([new Claim(ClaimTypes.Name, "operator")], "test"));

        var allowed = await StyloBotDashboardHub.IsAuthorizedAsync(
            context, LoginModeOptions(), new TestEnvironment("Production"));

        Assert.True(allowed);
    }

    /// <summary>
    ///     Login mode that is SELECTED but not fully configured (no username/hash) must not
    ///     take the login path — IsConfigured is false, so it falls through to the normal
    ///     deny. Guards against the fix accidentally widening the branch.
    /// </summary>
    [Fact]
    public async Task Login_mode_selected_but_unconfigured_still_denies()
    {
        var options = new StyloBotDashboardOptions();
        options.Auth.Mode = DashboardAuthMode.Login;   // no Username / PasswordHash
        Assert.False(options.Auth.IsConfigured);

        var allowed = await AuthorizeAsync(options, "Production");

        Assert.False(allowed);
    }

    private static StyloBotDashboardOptions LoginModeOptions()
    {
        var options = new StyloBotDashboardOptions();
        options.Auth.Mode = DashboardAuthMode.Login;
        options.Auth.Username = "operator";
        options.Auth.PasswordHash = "not-a-real-hash-value-for-test";
        Assert.True(options.Auth.IsConfigured, "fixture must be a configured login setup");
        return options;
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
