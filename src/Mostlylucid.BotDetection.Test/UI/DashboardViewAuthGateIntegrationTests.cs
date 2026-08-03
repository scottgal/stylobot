using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Services.Auth;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
/// End-to-end gate tests for the FOSS config-credential dashboard view-auth,
/// driving the real StyloBotDashboardMiddleware over TestServer: unauthenticated
/// HTML requests redirect to the login page, a correct login POST issues the auth
/// cookie, and a request carrying that cookie passes the gate.
/// </summary>
public sealed class DashboardViewAuthGateIntegrationTests : IAsyncDisposable
{
    private const string Password = "s3cret-pw";
    private static readonly string Hash = DashboardPasswordHasher.Hash(Password);
    private WebApplication? _app;

    [Fact]
    public async Task Unauthenticated_html_request_redirects_to_login()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/dashboard/traffic");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard/login", response.Headers.Location?.ToString());
    }

    [Fact]
    public async Task Login_page_renders_a_password_form()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/dashboard/login");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("type=\"password\"", html);
        Assert.Contains("name=\"username\"", html);
    }

    [Fact]
    public async Task Correct_login_post_issues_auth_cookie_and_redirects_to_dashboard()
    {
        var client = await StartAsync();

        var response = await PostLoginAsync(client, "admin", Password);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard", response.Headers.Location?.ToString());
        Assert.Contains(response.Headers.GetValues("Set-Cookie"),
            c => c.StartsWith("sb.dashboard.auth", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Wrong_password_does_not_issue_a_cookie()
    {
        var client = await StartAsync();

        var response = await PostLoginAsync(client, "admin", "wrong-pw");

        Assert.False(response.Headers.Contains("Set-Cookie")
                     && response.Headers.GetValues("Set-Cookie")
                         .Any(c => c.StartsWith("sb.dashboard.auth=", StringComparison.Ordinal)
                                   && !c.StartsWith("sb.dashboard.auth=;", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Request_with_auth_cookie_passes_the_gate()
    {
        var client = await StartAsync();

        var login = await PostLoginAsync(client, "admin", Password);
        var authCookie = login.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("sb.dashboard.auth", StringComparison.Ordinal))
            .Split(';')[0];

        var authed = new HttpRequestMessage(HttpMethod.Get, "/dashboard/traffic");
        authed.Headers.Add("Cookie", authCookie);
        var response = await client.SendAsync(authed);

        // The gate passed: NOT redirected back to the login page.
        Assert.NotEqual("/dashboard/login", response.Headers.Location?.ToString());
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ---- Full login → dashboard → logout flow ----

    [Fact]
    public async Task Full_login_dashboard_logout_flow()
    {
        var client = await StartAsync();

        // 1. Unauthenticated → redirected to login
        var unauth = await client.GetAsync("/dashboard/traffic");
        Assert.Equal(HttpStatusCode.Redirect, unauth.StatusCode);

        // 2. Login → get auth cookie
        var login = await PostLoginAsync(client, "admin", Password);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var authCookie = login.Headers.GetValues("Set-Cookie")
            .First(c => c.StartsWith("sb.dashboard.auth", StringComparison.Ordinal))
            .Split(';')[0];

        // 3. Authenticated → dashboard accessible
        var authedReq = new HttpRequestMessage(HttpMethod.Get, "/dashboard/traffic");
        authedReq.Headers.Add("Cookie", authCookie);
        var authed = await client.SendAsync(authedReq);
        Assert.NotEqual(HttpStatusCode.Redirect, authed.StatusCode);

        // 4. Logout → redirected to login
        var logoutReq = new HttpRequestMessage(HttpMethod.Get, "/dashboard/logout");
        logoutReq.Headers.Add("Cookie", authCookie);
        var logout = await client.SendAsync(logoutReq);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.EndsWith("/login", logout.Headers.Location?.ToString());

        // 5. After logout → dashboard redirects to login again
        var afterLogout = await client.GetAsync("/dashboard/traffic");
        Assert.Equal(HttpStatusCode.Redirect, afterLogout.StatusCode);
    }

    // ---- Dashboard API / partials return 401 JSON, not HTML redirect ----

    [Fact]
    public async Task Unauthenticated_api_request_returns_401_json()
    {
        var client = await StartAsync();
        var response = await client.GetAsync("/dashboard/api/summary");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Unauthorized", body);
    }

    [Fact]
    public async Task Unauthenticated_partials_request_returns_401_json()
    {
        var client = await StartAsync();
        var response = await client.GetAsync("/dashboard/partials/widget");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ---- CSRF validation ----

    [Fact]
    public async Task Login_post_without_csrf_token_is_rejected()
    {
        var client = await StartAsync();

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = Password
        });
        var response = await client.PostAsync("/dashboard/login", form);

        // Should NOT issue an auth cookie
        var setCookie = response.Headers.GetValues("Set-Cookie");
        Assert.DoesNotContain(setCookie,
            c => c.StartsWith("sb.dashboard.auth=", StringComparison.Ordinal)
                 && !c.StartsWith("sb.dashboard.auth=;", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Login_post_with_wrong_csrf_token_is_rejected()
    {
        var client = await StartAsync();

        // Get the login page to get a CSRF cookie, but submit a wrong token
        var page = await client.GetAsync("/dashboard/login");
        var csrfCookie = page.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.First(c => c.StartsWith("sb.login.csrf", StringComparison.Ordinal)).Split(';')[0]
            : string.Empty;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = "admin",
            ["password"] = Password,
            ["__csrf"] = "DEADBEEF000000000000000000000000000000000000000000000000000000"
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/dashboard/login") { Content = form };
        if (csrfCookie.Length > 0) request.Headers.Add("Cookie", csrfCookie);
        var response = await client.SendAsync(request);

        // Should NOT issue an auth cookie
        var setCookie = response.Headers.GetValues("Set-Cookie");
        Assert.DoesNotContain(setCookie,
            c => c.StartsWith("sb.dashboard.auth=", StringComparison.Ordinal)
                 && !c.StartsWith("sb.dashboard.auth=;", StringComparison.Ordinal));
    }

    // ---- Wrong username returns same error page (no timing leak) ----

    [Fact]
    public async Task Wrong_username_returns_same_error_as_wrong_password()
    {
        var client = await StartAsync();

        var wrongUser = await PostLoginAsync(client, "notadmin", Password);
        var wrongPass = await PostLoginAsync(client, "admin", "wrong-pw");

        Assert.Equal(wrongUser.StatusCode, wrongPass.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongUser.StatusCode);

        var bodyUser = await wrongUser.Content.ReadAsStringAsync();
        var bodyPass = await wrongPass.Content.ReadAsStringAsync();

        // Both show the same error text (ignore nonce differences)
        Assert.Contains("Invalid username or password", bodyUser);
        Assert.Contains("Invalid username or password", bodyPass);
    }

    // ---- Login page is reachable without auth ----

    [Fact]
    public async Task Login_page_is_always_reachable()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/dashboard/login");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ---- Auth.Mode=None (back-compat: no login gate) ----

    [Fact]
    public async Task Mode_none_allows_access_when_allow_unauth_is_true()
    {
        // This test uses a separate app instance with Mode=None + AllowUnauthenticatedAccess=true
        // to verify the back-compat path: no /login redirect, dashboard is open.
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddStyloBotDashboardRemote(new DashboardSourceOptions
        {
            Pull = new DashboardSourcePullOptions
            {
                Type = DashboardSourceType.Rest,
                Url = "http://gateway.test",
                TimeoutSeconds = 2
            }
        });
        builder.Services.AddHttpClient<GatewayApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new EmptyGatewayHandler());
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.AllowUnauthenticatedAccess = true;
            options.Auth.Mode = DashboardAuthMode.None;
        });

        var app = builder.Build();
        app.UseMiddleware<StyloBotDashboardMiddleware>();
        await app.StartAsync();

        var server = (TestServer)app.Services.GetRequiredService<IServer>();
        var client = new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };

        try
        {
            var response = await client.GetAsync("/dashboard/traffic");
            // Should NOT redirect to login; should be accessible (200 or the dashboard page)
            Assert.NotEqual("/dashboard/login", response.Headers.Location?.ToString());
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    // ---- Logout is always reachable ----

    [Fact]
    public async Task Logout_is_reachable_even_without_auth_cookie()
    {
        var client = await StartAsync();

        var response = await client.GetAsync("/dashboard/logout");

        // Logout always redirects to login (clears cookie regardless)
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.EndsWith("/login", response.Headers.Location?.ToString());
    }

    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string username, string password)
    {
        // Fetch the login page first, like a browser, to obtain the double-submit CSRF
        // cookie + hidden-field token, then POST them back together.
        var page = await client.GetAsync("/dashboard/login");
        var html = await page.Content.ReadAsStringAsync();
        var token = System.Text.RegularExpressions.Regex
            .Match(html, "name=\"__csrf\" value=\"([0-9A-Fa-f]+)\"").Groups[1].Value;
        var csrfCookie = page.Headers.TryGetValues("Set-Cookie", out var cookies)
            ? cookies.First(c => c.StartsWith("sb.login.csrf", StringComparison.Ordinal)).Split(';')[0]
            : string.Empty;

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["username"] = username,
            ["password"] = password,
            ["__csrf"] = token
        });
        var request = new HttpRequestMessage(HttpMethod.Post, "/dashboard/login") { Content = form };
        if (csrfCookie.Length > 0) request.Headers.Add("Cookie", csrfCookie);
        return await client.SendAsync(request);
    }

    private async Task<HttpClient> StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddStyloBotDashboardRemote(new DashboardSourceOptions
        {
            Pull = new DashboardSourcePullOptions
            {
                Type = DashboardSourceType.Rest,
                Url = "http://gateway.test",
                TimeoutSeconds = 2
            }
        });
        builder.Services.AddHttpClient<GatewayApiClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new EmptyGatewayHandler());
        builder.Services.AddControllersWithViews()
            .AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = false;
            options.Auth.Mode = DashboardAuthMode.Login;
            options.Auth.Username = "admin";
            options.Auth.PasswordHash = Hash;
        });

        _app = builder.Build();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        // Raw handler: does not auto-follow redirects, so we can assert on 302s.
        var server = (TestServer)_app.Services.GetRequiredService<IServer>();
        return new HttpClient(server.CreateHandler()) { BaseAddress = new Uri("http://localhost/") };
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
            await _app.DisposeAsync();
    }

    private sealed class EmptyGatewayHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = System.Net.Http.Json.JsonContent.Create(new { data = Array.Empty<object>() })
            };
            return Task.FromResult(response);
        }
    }
}
