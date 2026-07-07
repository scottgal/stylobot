using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.Common.Scheduling;
using Stylobot.Gateway.Data;
using Stylobot.Gateway.Health;
using Yarp.ReverseProxy.Configuration;
using Xunit;

namespace Stylobot.Gateway.Tests.Health;

/// <summary>
/// TDD tests for <see cref="UpstreamHealthProbeService"/>:
/// (a) healthy probe updates state, persists envelope, leaves DegradationAtom untouched;
/// (b) non-200 sets unhealthy, calls Invalidate;
/// (c) uncached cluster triggers DiscoverAsync;
/// (d) one cluster throwing does not stop sibling probe;
/// (e) Enabled=false means Subscribe is never called.
/// </summary>
public class UpstreamHealthProbeServiceTests : IDisposable
{
    // ── in-memory SQLite shared per test class ──────────────────────────────
    private readonly SqliteConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;

    public UpstreamHealthProbeServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var services = new ServiceCollection();
        services.AddDbContext<GatewayDbContext>(opts => opts.UseSqlite(_connection));
        var sp = services.BuildServiceProvider();

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

    private static IProxyConfigProvider MakeProxyConfig(
        params (string clusterId, string destId, string address)[] clusters)
    {
        var mock = new Mock<IProxyConfigProvider>();
        var proxyConfig = new Mock<IProxyConfig>();
        var clusterList = clusters
            .Select(c => new ClusterConfig
            {
                ClusterId = c.clusterId,
                Destinations = new Dictionary<string, DestinationConfig>
                {
                    [c.destId] = new DestinationConfig { Address = c.address },
                },
            })
            .ToList<ClusterConfig>();
        proxyConfig.Setup(cfg => cfg.Clusters).Returns(clusterList);
        mock.Setup(p => p.GetConfig()).Returns(proxyConfig.Object);
        return mock.Object;
    }

    private UpstreamHealthProbeService CreateSut(
        IUpstreamHealthEndpointDiscovery discovery,
        IActiveUpstreamProbeState probeState,
        IProxyConfigProvider proxyConfig,
        Func<HttpRequestMessage, HttpResponseMessage> handler,
        int probeIntervalSeconds = 0,
        bool enabled = true,
        IScheduleCoordinator? coordinator = null)
    {
        var options = Options.Create(new UpstreamHealthMonitorOptions
        {
            Enabled = enabled,
            ProbeIntervalSeconds = probeIntervalSeconds,
            ProbeTimeoutMs = 2000,
        });
        var client = new HttpClient(new StubHandler(handler));
        return new UpstreamHealthProbeService(
            discovery,
            probeState,
            options,
            proxyConfig,
            client,
            _scopeFactory,
            NullLogger<UpstreamHealthProbeService>.Instance,
            coordinator);
    }

    // ── Test (a): healthy probe + separate-lane invariant ───────────────────

    [Fact]
    public async Task OnTickAsync_Returns_Healthy_And_Persists_And_DoesNotTouch_DegradationAtom()
    {
        // Construct a real DegradationAtom -- NOT passed to the service.
        // This pins the separate-lane invariant: the tick must not feed
        // passive EWMA samples (DegradationAtom.RecordResponse).
        using var atom = new DegradationAtom();

        var discovery = new Mock<IUpstreamHealthEndpointDiscovery>();
        discovery
            .Setup(d => d.GetCached("c1"))
            .Returns(new DiscoveredHealthEndpoint("/health", null, DateTimeOffset.UtcNow));

        var probeState = new ActiveUpstreamProbeState();
        var proxy = MakeProxyConfig(("c1", "primary", "http://upstream:5000"));

        var sut = CreateSut(
            discovery.Object,
            probeState,
            proxy,
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        await sut.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // probe state updated with healthy snapshot
        var snap = probeState.Latest("c1");
        snap.Should().NotBeNull();
        snap!.Status.Should().Be("healthy");
        snap.LatencyMs.Should().BeGreaterThanOrEqualTo(0);
        snap.FailureReason.Should().BeNull();

        // envelope persisted to DB
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var entity = await db.Destinations.FindAsync(new object[] { "c1", "primary" });
        entity.Should().NotBeNull();
        entity!.Health.Should().Contain("\"status\":\"healthy\"");

        // separate lane: atom is completely untouched
        atom.TotalSamples.Should().Be(0);
    }

    // ── Test (b): non-200 → unhealthy + reason + Invalidate called ──────────

    [Fact]
    public async Task OnTickAsync_Non200_Sets_Unhealthy_And_Persists_And_Invalidates_Discovery()
    {
        var discovery = new Mock<IUpstreamHealthEndpointDiscovery>();
        discovery
            .Setup(d => d.GetCached("c1"))
            .Returns(new DiscoveredHealthEndpoint("/health", null, DateTimeOffset.UtcNow));

        var probeState = new ActiveUpstreamProbeState();
        var proxy = MakeProxyConfig(("c1", "primary", "http://upstream:5000"));

        var sut = CreateSut(
            discovery.Object,
            probeState,
            proxy,
            _ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        await sut.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        var snap = probeState.Latest("c1");
        snap.Should().NotBeNull();
        snap!.Status.Should().Be("unhealthy");
        snap.FailureReason.Should().NotBeNull();

        // Invalidate must be called so next tick re-discovers
        discovery.Verify(d => d.Invalidate("c1"), Times.Once());

        // persisted envelope has status=unhealthy
        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<GatewayDbContext>();
        var entity = await db.Destinations.FindAsync(new object[] { "c1", "primary" });
        entity.Should().NotBeNull();
        entity!.Health.Should().Contain("\"status\":\"unhealthy\"");
    }

    // ── Test (c): uncached cluster triggers DiscoverAsync ───────────────────

    [Fact]
    public async Task OnTickAsync_Uncached_Triggers_DiscoverAsync()
    {
        var discovery = new Mock<IUpstreamHealthEndpointDiscovery>();
        discovery
            .Setup(d => d.GetCached("c1"))
            .Returns((DiscoveredHealthEndpoint?)null);
        discovery
            .Setup(d => d.DiscoverAsync("c1", "http://upstream:5000", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new DiscoveredHealthEndpoint("/health", null, DateTimeOffset.UtcNow));

        var probeState = new ActiveUpstreamProbeState();
        var proxy = MakeProxyConfig(("c1", "primary", "http://upstream:5000"));

        var sut = CreateSut(
            discovery.Object,
            probeState,
            proxy,
            _ => new HttpResponseMessage(HttpStatusCode.OK));

        await sut.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        discovery.Verify(
            d => d.DiscoverAsync("c1", "http://upstream:5000", It.IsAny<CancellationToken>()),
            Times.Once());
        probeState.Latest("c1").Should().NotBeNull();
        probeState.Latest("c1")!.Status.Should().Be("healthy");
    }

    // ── Test (d): per-cluster exception does not stop sibling probe ──────────

    [Fact]
    public async Task OnTickAsync_PerCluster_Exception_Does_Not_Stop_Sibling_Probe()
    {
        var discovery = new Mock<IUpstreamHealthEndpointDiscovery>();
        // c1's GetCached throws -- simulates any unexpected failure inside the per-cluster block
        discovery
            .Setup(d => d.GetCached("c1"))
            .Throws(new InvalidOperationException("forced probe error"));
        // c2 resolves normally
        discovery
            .Setup(d => d.GetCached("c2"))
            .Returns(new DiscoveredHealthEndpoint("/health", null, DateTimeOffset.UtcNow));

        var probeState = new ActiveUpstreamProbeState();
        var proxy = MakeProxyConfig(
            ("c1", "primary", "http://bad-upstream:5000"),
            ("c2", "primary", "http://good-upstream:5000"));

        var sut = CreateSut(
            discovery.Object,
            probeState,
            proxy,
            req => req.RequestUri!.Host.Contains("good")
                ? new HttpResponseMessage(HttpStatusCode.OK)
                : new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));

        // Must not throw -- the per-cluster catch must absorb the c1 failure.
        await sut.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // c1 failed before probe state was written
        probeState.Latest("c1").Should().BeNull();
        // c2 was still probed and recorded
        var snap = probeState.Latest("c2");
        snap.Should().NotBeNull();
        snap!.Status.Should().Be("healthy");
    }

    // ── Test (e): Enabled=false → Subscribe not called ──────────────────────

    [Fact]
    public void Ctor_When_Disabled_Does_Not_Subscribe_To_Coordinator()
    {
        var discovery = new Mock<IUpstreamHealthEndpointDiscovery>();
        var probeState = new ActiveUpstreamProbeState();
        var proxy = MakeProxyConfig();
        var coordinator = new Mock<IScheduleCoordinator>();

        var options = Options.Create(new UpstreamHealthMonitorOptions { Enabled = false });
        var client = new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));

        _ = new UpstreamHealthProbeService(
            discovery.Object,
            probeState,
            options,
            proxy,
            client,
            _scopeFactory,
            NullLogger<UpstreamHealthProbeService>.Instance,
            coordinator.Object);

        coordinator.Verify(
            c => c.Subscribe(
                It.IsAny<TickCadence>(),
                It.IsAny<string>(),
                It.IsAny<CostHint>(),
                It.IsAny<Func<DateTimeOffset, CancellationToken, Task>>()),
            Times.Never());
    }

    // ── Test (f): ProbeIntervalSeconds throttle: skip within window, fire after ──

    [Fact]
    public async Task OnTickAsync_Respects_ProbeIntervalSeconds_Throttle()
    {
        var discovery = new Mock<IUpstreamHealthEndpointDiscovery>();
        discovery
            .Setup(d => d.GetCached("c1"))
            .Returns(new DiscoveredHealthEndpoint("/health", null, DateTimeOffset.UtcNow));

        var probeState = new ActiveUpstreamProbeState();
        var proxy = MakeProxyConfig(("c1", "primary", "http://upstream:5000"));

        var callCount = 0;
        var sut = CreateSut(
            discovery.Object,
            probeState,
            proxy,
            _ =>
            {
                callCount++;
                return new HttpResponseMessage(HttpStatusCode.OK);
            },
            probeIntervalSeconds: 60);

        var t0 = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

        // First tick: no prior probe, should fire.
        await sut.OnTickAsync(t0, CancellationToken.None);
        callCount.Should().Be(1, "first tick must always probe");

        // Second tick at t0+30s: within the 60s interval, must be skipped.
        await sut.OnTickAsync(t0.AddSeconds(30), CancellationToken.None);
        callCount.Should().Be(1, "tick within ProbeIntervalSeconds must be skipped");

        // Third tick at t0+61s: past the interval, must probe again.
        await sut.OnTickAsync(t0.AddSeconds(61), CancellationToken.None);
        callCount.Should().Be(2, "tick after ProbeIntervalSeconds must fire again");
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
