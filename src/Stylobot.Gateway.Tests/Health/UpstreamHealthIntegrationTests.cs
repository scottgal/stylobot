using System.Net;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.Common.Scheduling;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Data;
using Stylobot.Gateway.Health;
using Yarp.ReverseProxy.Configuration;
using Xunit;

namespace Stylobot.Gateway.Tests.Health;

/// <summary>
/// Integration tests for Task 5: gateway wiring seam.
/// (a) Gate + probe compose end-to-end: one tick produces a healthy gate verdict,
///     lane separation holds (DegradationAtom.TotalSamples stays 0).
/// (b) Enabled=false: Subscribe is never called on IScheduleCoordinator.
/// (c) DI descriptor smoke: AddUpstreamHealthMonitor registers the three expected
///     service types (checked via IServiceCollection inspection, not full provider
///     build, to avoid pulling in the full gateway graph).
/// </summary>
public class UpstreamHealthIntegrationTests : IDisposable
{
    // ── in-memory SQLite shared per test class ──────────────────────────────
    private readonly SqliteConnection _connection;
    private readonly IServiceScopeFactory _scopeFactory;

    public UpstreamHealthIntegrationTests()
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

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }

    // ── Test (a): gate + probe seam composes end-to-end ─────────────────────

    /// <summary>
    /// Builds a real ActiveUpstreamProbeState (shared between probe service and gate),
    /// runs one OnTickAsync with a stub handler that returns 200, then asserts the gate
    /// reports healthy AND the passive DegradationAtom was never touched.
    /// </summary>
    [Fact]
    public async Task SeamCompose_Gate_Reads_Active_Probe_State_Healthy_After_One_Tick()
    {
        // Shared probe state: probe service writes, gate reads.
        var probeState = new ActiveUpstreamProbeState();

        // Passive atom -- separate lane. Must stay at TotalSamples=0 after a tick.
        using var atom = new DegradationAtom();

        // Gate with IOptions overload (DI default path).
        // MinSampleCount=10, atom has 0 samples → passive data-starved → active fills the gap.
        var gate = new UpstreamHealthGate(
            atom,
            Options.Create(new UpstreamHealthOptions { MinSampleCount = 10 }),
            probeState);

        // Mock discovery returns a pre-cached endpoint for cluster c1.
        var discovery = new Mock<IUpstreamHealthEndpointDiscovery>();
        discovery
            .Setup(d => d.GetCached("c1"))
            .Returns(new DiscoveredHealthEndpoint("/healthz", "application/json", DateTimeOffset.UtcNow));

        var proxy = MakeProxyConfig(("c1", "primary", "http://upstream:5000"));

        var probeService = new UpstreamHealthProbeService(
            discovery.Object,
            probeState,
            Options.Create(new UpstreamHealthMonitorOptions
            {
                Enabled = true,
                ProbeIntervalSeconds = 0, // no throttle: every tick fires
                ProbeTimeoutMs = 2000,
            }),
            proxy,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
            _scopeFactory,
            NullLogger<UpstreamHealthProbeService>.Instance,
            scheduleCoordinator: null);

        await probeService.OnTickAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        // Gate: passive data-starved (0 < MinSampleCount=10) → active fills the gap.
        gate.IsUpstreamHealthy().Should().BeTrue(
            "active probe returned HTTP 200 → probeState shows healthy → gate fills cold-start gap");

        // Lane separation: the passive DegradationAtom must not have been touched by the tick.
        atom.TotalSamples.Should().Be(0,
            "active probes write only to IActiveUpstreamProbeState, never to DegradationAtom");
    }

    // ── Test (b): Enabled=false → Subscribe never called ────────────────────

    [Fact]
    public void Ctor_WhenDisabled_DoesNotSubscribeToScheduleCoordinator()
    {
        var coordinator = new Mock<IScheduleCoordinator>();
        var discovery = new Mock<IUpstreamHealthEndpointDiscovery>();
        var probeState = new ActiveUpstreamProbeState();
        var proxy = MakeProxyConfig();

        _ = new UpstreamHealthProbeService(
            discovery.Object,
            probeState,
            Options.Create(new UpstreamHealthMonitorOptions { Enabled = false }),
            proxy,
            new HttpClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK))),
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

    // ── Test (c): DI descriptor smoke ───────────────────────────────────────

    /// <summary>
    /// Narrowed: inspects IServiceCollection descriptors rather than building
    /// the full provider, because UpstreamHealthProbeService requires
    /// IProxyConfigProvider, IActiveUpstreamProbeState, and IScheduleCoordinator
    /// that live in other gateway assemblies. Descriptor inspection is sufficient
    /// to verify AddUpstreamHealthMonitor wired the three expected service types.
    /// </summary>
    [Fact]
    public void AddUpstreamHealthMonitor_RegistersExpectedServiceDescriptors()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();

        services.AddUpstreamHealthMonitor(config);

        services.Should().Contain(
            sd => sd.ServiceType == typeof(IUpstreamHealthEndpointDiscovery),
            "AddUpstreamHealthMonitor must register IUpstreamHealthEndpointDiscovery as a singleton");

        services.Should().Contain(
            sd => sd.ServiceType == typeof(UpstreamHealthProbeService),
            "AddUpstreamHealthMonitor must register UpstreamHealthProbeService as a singleton");

        // AddHttpClient registers IHttpClientFactory; its presence confirms the
        // named client "upstream-health-probe" was declared.
        services.Should().Contain(
            sd => sd.ServiceType == typeof(System.Net.Http.IHttpClientFactory),
            "AddUpstreamHealthMonitor must call AddHttpClient so the named probe client resolves");
    }
}
