using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Api.Auth;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Dashboard.Contracts;
using Mostlylucid.BotDetection.Dashboard.Data.Queries;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.Traffic;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>Real SQLite → authenticated HTTP API → MVC/HTMX, with no viewer database or legacy fetches.</summary>
public sealed class ApiTrafficReliabilityTests : IAsyncDisposable
{
    private const string Key = "traffic-integration-key";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "traffic-pilot-" + Guid.NewGuid().ToString("N"));
    private readonly FixedClock _clock = new();
    private readonly IOptions<BotDetectionOptions> _options;
    private readonly SqliteDashboardEventStore _store;
    private WebApplication? _backend;
    private WebApplication? _viewer;
    private readonly List<string> _logs = [];

    public ApiTrafficReliabilityTests()
    {
        Directory.CreateDirectory(_directory);
        _options = Options.Create(new BotDetectionOptions { DatabasePath = Path.Combine(_directory, "backend.db") });
        _options.Value.Classification.BotFloor = 0.95;
        _options.Value.ApiKeys["test"] = new ApiKeyConfig { Key = Key, Enabled = true };
        _store = new(NullLogger<SqliteDashboardEventStore>.Instance, _options);
    }

    private Task Seed(string domain, double probability, string path = "/example", string? botType = null, int minutesAgo = 5) =>
        _store.AddDetectionAsync(new DashboardDetectionEvent
        {
            RequestId = Guid.NewGuid().ToString("N"), PrimarySignature = Guid.NewGuid().ToString("N"),
            Timestamp = _clock.GetUtcNow().AddMinutes(-minutesAgo).UtcDateTime, Domain = domain,
            BotProbability = probability, IsBot = probability >= .95, Confidence = .99,
            BotType = botType, Method = "GET", Path = path, RiskBand = "Low", ThreatBand = "High"
        });

    [Fact]
    public async Task All_slices_use_same_window_domains_audience_and_configured_bot_floor()
    {
        await Seed("one.test", .99);
        await Seed("one.test", .90);
        await Seed("two.test", .99);
        await Seed("one.test", .99, botType: "Internal");
        await Seed("one.test", .99, minutesAgo: 90);
        var service = new TrafficQueryService(_store, _options, _clock);
        var snapshot = await service.ReadAsync(new("1h", Domains: ["ONE.test", " one.test "]), default);
        snapshot.Domains.Should().Equal("one.test");
        snapshot.Summary.Requests.Should().Be(2);
        snapshot.Summary.Bots.Should().Be(1);
        snapshot.Timeline.Sum(b => b.Requests).Should().Be(2);
        snapshot.Detections.Should().HaveCount(2).And.OnlyContain(d => d.Domain == "one.test");
        snapshot.Detections.Count(d => d.IsBot).Should().Be(1);
        var all = await service.ReadAsync(new("1h", Domains: ["one.test"], IncludeInternal: true), default);
        all.Summary.Requests.Should().Be(3);
        all.Timeline.Sum(b => b.Requests).Should().Be(3);
        all.Detections.Should().HaveCount(3);
        (await _store.GetThreatsAsync(domains: ["one.test"])).Should().HaveCount(4);
        await Seed("one.test", .99, minutesAgo: 0);
        var boundary = await service.ReadAsync(new("custom", _clock.GetUtcNow().AddHours(-1), _clock.GetUtcNow(), ["one.test"], true), default);
        boundary.Summary.Requests.Should().Be(3);
        boundary.Detections.Should().HaveCount(3, "the upper time boundary must be exclusive for every slice");
        // Existing callers did not request the new audience filter: retain their historical behavior.
        (await _store.GetDetectionsAsync(new DashboardFilter { Limit = 50 })).Should().HaveCount(6);
    }

    [Theory]
    [InlineData("bad", 50)]
    [InlineData("24h", 0)]
    [InlineData("24h", 201)]
    [InlineData("custom", 50)]
    public async Task Invalid_queries_fail_before_storage(string window, int limit)
    {
        var store = new Mock<IDashboardEventStore>(MockBehavior.Strict);
        var service = new TrafficQueryService(store.Object, _options, _clock);
        await Assert.ThrowsAsync<TrafficQueryValidationException>(() => service.ReadAsync(new(window, Limit: limit), default));
        store.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Missing_required_store_slices_fail_instead_of_fabricating_zeroes()
    {
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.ComposeBatchAsync(It.IsAny<DashboardBatchRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DashboardDatasetBundle(null, [], null, null, null));
        store.Setup(s => s.GetDetectionsAsync(It.IsAny<DashboardFilter>(), It.IsAny<CancellationToken>())).ReturnsAsync([]);
        var service = new TrafficQueryService(store.Object, _options, _clock);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ReadAsync(new(), default));
    }

    [Fact]
    public async Task Cancellation_is_not_an_empty_snapshot()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var service = new TrafficQueryService(new Mock<IDashboardEventStore>(MockBehavior.Strict).Object, _options, _clock);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ReadAsync(new(), cancelled.Token));
    }

    [Fact]
    public async Task Api_requires_key_validates_and_redacts_logged_storage_failures()
    {
        var failing = new Mock<IDashboardEventStore>();
        failing.Setup(s => s.ComposeBatchAsync(It.IsAny<DashboardBatchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("private database path /secret/backend.db"));
        var client = await StartBackend(failing.Object);
        (await client.GetAsync("/api/v1/dashboard/traffic")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        client.DefaultRequestHeaders.Add(ApiKeyAuthenticationHandler.HeaderName, Key);
        (await client.GetAsync("/api/v1/dashboard/traffic?window=bad")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var response = await client.GetAsync("/api/v1/dashboard/traffic");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("traceId").And.NotContain("secret").And.NotContain("private database");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        _logs.Should().Contain(s => s.Contains("Traffic snapshot read failed") && s.Contains("TraceId:"));
    }

    [Fact]
    public async Task Viewer_renders_real_api_full_page_and_fragment_at_custom_base_and_path_base()
    {
        await Seed("one.test", .99, "<script>alert(1)</script>");
        await Seed("two.test", .1);
        await StartBackend();
        var client = await StartViewer();
        var response = await client.GetAsync("/proxy/ops/traffic?window=1h&domain=one.test");
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, html);
        html.Should().Contain("<!doctype html>").And.Contain("data-state=\"ready\"")
            .And.Contain("&lt;script&gt;").And.NotContain("<script>alert(1)")
            .And.Contain("/proxy/ops/traffic").And.NotContain(Key);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/proxy/ops/traffic?window=1h&domain=missing.test");
        request.Headers.Add("HX-Request", "true");
        request.Headers.Add("HX-Current-URL", "http://localhost/proxy/ops/traffic?window=1h&domain=missing.test");
        var fragment = await client.SendAsync(request);
        var content = await fragment.Content.ReadAsStringAsync();
        fragment.StatusCode.Should().Be(HttpStatusCode.OK, content);
        content.Should().Contain("data-state=\"empty\"").And.Contain("No traffic in this time window.").And.NotContain("<!doctype");
        fragment.Headers.Vary.Should().Contain("HX-Request");
        fragment.Headers.GetValues("HX-Push-Url").Should().Contain("false");
        _viewer!.Services.GetService<IDashboardEventStore>().Should().BeOfType<RemoteDashboardEventStore>();
        (await client.GetAsync("/proxy/ApiTraffic/Index")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Viewer_failure_is_unavailable_without_successful_zero_counters()
    {
        var failing = new Mock<IDashboardEventStore>();
        failing.Setup(s => s.ComposeBatchAsync(It.IsAny<DashboardBatchRequest>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("private failure"));
        await StartBackend(failing.Object);
        var client = await StartViewer();
        var response = await client.GetAsync("/proxy/ops/traffic");
        var html = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable, html);
        html.Should().Contain("data-state=\"unavailable\"").And.NotContain("data-sb-total")
            .And.NotContain("private failure").And.NotContain(Key);
    }

    [Fact]
    public async Task Pilot_uses_existing_login_gate_before_backend_fetch()
    {
        await StartBackend();
        var client = await StartViewer(login: true);
        var response = await client.GetAsync("/proxy/ops/traffic");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.ToString().Should().Be("/proxy/ops/login");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/proxy/ops/traffic");
        request.Headers.Add("HX-Request", "true");
        var fragment = await client.SendAsync(request);
        fragment.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        fragment.Headers.Location.Should().BeNull();
        var login = await client.GetAsync("/proxy/ops/login");
        var html = await login.Content.ReadAsStringAsync();
        html.Should().Contain("action=\"/proxy/ops/login\"");
        var token = System.Text.RegularExpressions.Regex.Match(html, "name=\"__csrf\" value=\"([0-9A-Fa-f]+)\"").Groups[1].Value;
        var csrfCookie = login.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("sb.login.csrf")).Split(';')[0];
        using var post = new HttpRequestMessage(HttpMethod.Post, "/proxy/ops/login")
        { Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "admin", ["password"] = "test-password", ["__csrf"] = token }) };
        post.Headers.Add("Cookie", csrfCookie);
        var signedIn = await client.SendAsync(post);
        signedIn.StatusCode.Should().Be(HttpStatusCode.Redirect);
        signedIn.Headers.Location!.ToString().Should().Be("/proxy/ops");
        signedIn.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("sb.dashboard.auth") && c.Contains("path=/proxy/ops"));
        var authCookie = signedIn.Headers.GetValues("Set-Cookie").First(c => c.StartsWith("sb.dashboard.auth")).Split(';')[0];
        using var authenticated = new HttpRequestMessage(HttpMethod.Get, "/proxy/ops/traffic");
        authenticated.Headers.Add("Cookie", authCookie);
        (await client.SendAsync(authenticated)).StatusCode.Should().Be(HttpStatusCode.OK);
        using var logout = new HttpRequestMessage(HttpMethod.Get, "/proxy/ops/logout");
        logout.Headers.Add("Cookie", authCookie);
        var signedOut = await client.SendAsync(logout);
        signedOut.Headers.Location!.ToString().Should().Be("/proxy/ops/login");
        signedOut.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("sb.dashboard.auth") && c.Contains("path=/proxy/ops"));
    }

    private async Task<HttpClient> StartBackend(IDashboardEventStore? store = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(new CaptureLogs(_logs));
        builder.Services.AddSingleton(_options);
        builder.Services.Configure<BotDetectionOptions>(o => o.ApiKeys["test"] = new ApiKeyConfig { Key = Key, Enabled = true });
        builder.Services.AddSingleton<IApiKeyStore, InMemoryApiKeyStore>();
        builder.Services.AddAuthentication(ApiKeyAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, _ => { });
        builder.Services.AddAuthorization(o => o.AddPolicy(ApiKeyAuthenticationHandler.SchemeName,
            p => p.AddAuthenticationSchemes(ApiKeyAuthenticationHandler.SchemeName).RequireAuthenticatedUser()));
        builder.Services.AddSingleton<TimeProvider>(_clock);
        builder.Services.AddSingleton<IDashboardEventStore>(store ?? _store);
        builder.Services.AddScoped<TrafficQueryService>();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.TypeInfoResolverChain.Insert(0, Mostlylucid.BotDetection.Api.StyloBotJsonContext.Default));
        _backend = builder.Build();
        _backend.UseAuthentication();
        _backend.UseAuthorization();
        _backend.MapDashboardTrafficEndpoints();
        await _backend.StartAsync();
        return _backend.GetTestClient();
    }

    private async Task<HttpClient> StartViewer(bool login = false)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders().AddProvider(new CaptureLogs(_logs));
        builder.Services.AddStyloBotDashboardRemote(new DashboardSourceOptions
        {
            Pull = new() { Type = DashboardSourceType.Rest, Url = "http://backend.test", ApiKey = Key }
        });
        builder.Services.AddStyloBotDashboard(o =>
        {
            o.BasePath = "/ops"; o.UseApiTraffic = true; o.AllowUnauthenticatedAccess = !login;
            if (login) { o.Auth.Mode = DashboardAuthMode.Login; o.Auth.Username = "admin"; o.Auth.PasswordHash = Mostlylucid.BotDetection.UI.Services.Auth.DashboardPasswordHasher.Hash("test-password"); }
        });
        builder.Services.AddHttpClient<TrafficApiClient>().ConfigurePrimaryHttpMessageHandler(() => _backend!.GetTestServer().CreateHandler());
        _viewer = builder.Build();
        _viewer.UsePathBase("/proxy");
        _viewer.UseMiddleware<StyloBotDashboardMiddleware>();
        _viewer.MapStyloBotDashboardPages();
        _viewer.MapControllerRoute("direct", "{controller}/{action}");
        await _viewer.StartAsync();
        return _viewer.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_viewer is not null) await _viewer.DisposeAsync();
        if (_backend is not null) await _backend.DisposeAsync();
        await _store.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
    }

    private sealed class FixedClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 17, 12, 34, 20, TimeSpan.Zero);
    }

    private sealed class CaptureLogs(List<string> logs) : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => new Capture(logs);
        public void Dispose() { }
        private sealed class Capture(List<string> logs) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            { lock (logs) logs.Add(formatter(state, exception)); }
        }
    }
}
