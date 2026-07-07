using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stylobot.Gateway.Data;
using Stylobot.Gateway.Health;
using Xunit;

namespace Stylobot.Gateway.Tests.Health;

/// <summary>
/// TDD tests for UpstreamHealthEndpointDiscovery:
/// probe order, all-fail null, cache/invalidate lifecycle, persist+warm, post-invalidate no-rewarm.
/// </summary>
public class UpstreamHealthEndpointDiscoveryTests : IDisposable
{
    // ── in-memory SQLite shared per test class ──────────────────────────────
    private readonly SqliteConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;

    public UpstreamHealthEndpointDiscoveryTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(opts => opts.UseSqlite(_connection));
        var sp = services.BuildServiceProvider();

        // Build schema once per test class instance.
        using var scope = sp.CreateScope();
        scope.ServiceProvider.GetRequiredService<GatewayDbContext>().Database.EnsureCreated();

        _scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
    }

    public void Dispose()
    {
        _connection.Close();
        _connection.Dispose();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private UpstreamHealthEndpointDiscovery CreateSut(
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        string[]? candidatePaths = null)
    {
        var paths = candidatePaths ?? ["/health", "/healthz", "/livez"];
        var options = Options.Create(new UpstreamHealthMonitorOptions
        {
            CandidatePaths = paths,
            ProbeTimeoutMs = 2000,
        });
        var client = new HttpClient(new StubHttpMessageHandler(handler));
        return new UpstreamHealthEndpointDiscovery(
            client, options, _scopeFactory, NullLogger<UpstreamHealthEndpointDiscovery>.Instance);
    }

    // ── Test 1: first 200 wins ───────────────────────────────────────────────

    [Fact]
    public async Task DiscoverAsync_Returns_First200_Path_When_Earlier_Candidates_Return_404()
    {
        var sut = CreateSut(req =>
        {
            return req.RequestUri!.AbsolutePath switch
            {
                "/health" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/healthz" => new HttpResponseMessage(HttpStatusCode.NotFound),
                "/livez" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("") { Headers = { ContentType = new("application/json") } },
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound),
            };
        });

        var result = await sut.DiscoverAsync("c1", "http://upstream", CancellationToken.None);

        result.Should().NotBeNull();
        result!.Path.Should().Be("/livez");
        result.ContentType.Should().Contain("application/json");
    }

    // ── Test 2: all candidates fail → null ──────────────────────────────────

    [Fact]
    public async Task DiscoverAsync_Returns_Null_When_All_Candidates_Return_404()
    {
        var sut = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        var result = await sut.DiscoverAsync("c2", "http://upstream", CancellationToken.None);

        result.Should().BeNull();
    }

    // ── Test 3: GetCached returns discovered; Invalidate clears it ──────────

    [Fact]
    public async Task GetCached_Returns_Discovered_Value_And_Invalidate_Clears_It()
    {
        var sut = CreateSut(req =>
            req.RequestUri!.AbsolutePath == "/livez"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.NotFound),
            candidatePaths: ["/health", "/livez"]);

        await sut.DiscoverAsync("c3", "http://upstream", CancellationToken.None);

        sut.GetCached("c3").Should().NotBeNull();
        sut.GetCached("c3")!.Path.Should().Be("/livez");

        sut.Invalidate("c3");

        sut.GetCached("c3").Should().BeNull();
    }

    // ── Test 4: fresh instance warms from persistence ────────────────────────

    [Fact]
    public async Task GetCached_On_FreshInstance_Warms_From_Persistence_After_Discover()
    {
        // Instance A: discover + persist.
        var sutA = CreateSut(req =>
            req.RequestUri!.AbsolutePath == "/healthz"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.NotFound),
            candidatePaths: ["/health", "/healthz"]);

        var discovered = await sutA.DiscoverAsync("c4", "http://upstream", CancellationToken.None);
        discovered.Should().NotBeNull();
        discovered!.Path.Should().Be("/healthz");

        // Instance B: same scope factory, fresh caches.
        var sutB = CreateSut(_ => new HttpResponseMessage(HttpStatusCode.NotFound));

        // First GetCached on B should warm from DB.
        var warmed = sutB.GetCached("c4");
        warmed.Should().NotBeNull();
        warmed!.Path.Should().Be("/healthz");
    }

    // ── Test 5: post-invalidate on same instance does NOT re-warm ───────────

    [Fact]
    public async Task GetCached_After_Invalidate_Returns_Null_On_Same_Instance_Even_Though_Persisted()
    {
        var sut = CreateSut(req =>
            req.RequestUri!.AbsolutePath == "/health"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.NotFound));

        await sut.DiscoverAsync("c5", "http://upstream", CancellationToken.None);

        sut.Invalidate("c5");

        // Same instance must not re-warm from DB after Invalidate.
        sut.GetCached("c5").Should().BeNull();
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
