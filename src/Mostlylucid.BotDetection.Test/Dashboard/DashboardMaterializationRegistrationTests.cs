using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Task 6: resolution-tests the out-of-request materialization DI wiring
///     (extracted as <c>AddDashboardMaterialization</c>). The easy thing to get
///     wrong is the singleton content cache capturing the SCOPED composer — so this
///     builds the provider with ValidateScopes + ValidateOnBuild (which fails at
///     build on a captive dependency or any unresolvable service) and then actually
///     composes through the scope factory.
/// </summary>
public sealed class DashboardMaterializationRegistrationTests
{
    private static readonly DashboardPageManifest Traffic = new("dashboard.traffic", new[] { "summary" });
    private static DashboardPageWindow Window() => new(null, null, "all", null, null, 500, 60);

    private static ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // BindConfiguration (inside AddDashboardMaterialization) needs IConfiguration present,
        // even an empty one -- pre-existing gap this test never hit until options resolution
        // was exercised on every path (now true since the coordinator also needs manifests).
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        // The deps the materialization registration consumes:
        services.AddScoped<IDashboardPageComposer, FakeComposer>();   // SCOPED, like the real composer
        services.AddSingleton<IDashboardChangeCursor, FakeCursor>();
        services.AddSingleton<IDashboardPageManifestSource, DefaultDashboardPageManifestSource>();

        // Code under test — the actual AddStyloBotDashboard wiring, extracted.
        services.AddDashboardMaterialization();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    /// <summary>
    ///     Same wiring, but with <c>DashboardSourceOptions</c> present — i.e. a REMOTE-MODE
    ///     host (marketing site / stylobot-ui), where <c>AddStyloBotDashboardRemote</c> runs
    ///     before <c>AddStyloBotDashboard</c>.
    /// </summary>
    private static ServiceCollection RemoteModeServices()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<IDashboardPageComposer, FakeComposer>();
        services.AddSingleton<IDashboardChangeCursor, FakeCursor>();
        services.AddSingleton<IDashboardPageManifestSource, DefaultDashboardPageManifestSource>();
        // The remote-mode marker the guard probes for.
        services.AddSingleton(new Mostlylucid.BotDetection.UI.Adapters.Remote.DashboardSourceOptions());
        services.AddDashboardMaterialization();
        return services;
    }

    /// <summary>
    ///     PROD DEFECT GUARD. Three website replicas, no session affinity, each running its
    ///     own tick materializer with its own warm state — a request round-robined into
    ///     whichever pod happened to be warm, so the dashboard showed stale/inconsistent data
    ///     at random. The tick loop must not run on a remote-mode host.
    /// </summary>
    [Fact]
    public void Remote_mode_host_does_not_register_the_tick_materializer()
    {
        var services = RemoteModeServices();

        Assert.DoesNotContain(
            services,
            d => d.ServiceType == typeof(IHostedService)
                 && d.ImplementationFactory is not null
                 && d.ImplementationFactory.Method.ReturnType == typeof(DashboardMaterializerCoordinator));
    }

    /// <summary>
    ///     THE OTHER HALF, and the reason this is guarded at the hosted service rather than
    ///     at the <c>AddDashboardMaterialization()</c> call site: <c>TrafficController</c> and
    ///     <c>VisitorsController</c> take <see cref="IDashboardContentCache"/> as a REQUIRED
    ///     ctor parameter, so dropping the registration would 500 /dashboard/traffic and
    ///     /dashboard/visitors on every request — trading an intermittent staleness bug for a
    ///     certain outage on two of the pages that exhibited it.
    ///     <para>
    ///         Mirrors the <c>SignatureAggregateCacheWarmupService</c> precedent, whose own
    ///         comment states the cache "stays registered (satisfies DI for middleware /
    ///         view-components) but stays empty, so all read paths cleanly fall through to
    ///         IDashboardEventStore".
    ///     </para>
    /// </summary>
    [Fact]
    public async Task Remote_mode_host_still_registers_the_content_cache_so_controllers_resolve()
    {
        var services = RemoteModeServices();
        // await using: the cache is IAsyncDisposable only (SlidingCacheAtom-backed), so the
        // provider must be disposed async -- same as the sibling test below.
        await using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        Assert.NotNull(provider.GetService<IDashboardContentCache>());
        // The coordinator singleton also stays resolvable for out-of-band MarkDirtyAsync
        // callers; it simply never ticks.
        Assert.NotNull(provider.GetService<DashboardMaterializerCoordinator>());
    }

    /// <summary>
    ///     Parity guard: a LOCAL host must still get the tick materializer. Without this, a
    ///     regression that skipped it everywhere would look green.
    /// </summary>
    [Fact]
    public void Local_mode_host_still_registers_the_tick_materializer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddScoped<IDashboardPageComposer, FakeComposer>();
        services.AddSingleton<IDashboardChangeCursor, FakeCursor>();
        services.AddSingleton<IDashboardPageManifestSource, DefaultDashboardPageManifestSource>();
        // NO DashboardSourceOptions -> local host.
        services.AddDashboardMaterialization();

        Assert.Contains(
            services,
            d => d.ServiceType == typeof(IHostedService)
                 && d.ImplementationFactory is not null
                 && d.ImplementationFactory.Method.ReturnType == typeof(DashboardMaterializerCoordinator));
    }

    [Fact]
    public async Task Content_cache_resolves_as_singleton_and_composes_through_a_scope()
    {
        // await using: the cache is IAsyncDisposable (SlidingCacheAtom-backed), so the
        // provider must be disposed async — same as the host does in production.
        await using var sp = BuildProvider(); // ValidateOnBuild proves: no captive dep, all deps resolvable

        var cache = sp.GetRequiredService<IDashboardContentCache>();
        Assert.Same(cache, sp.GetRequiredService<IDashboardContentCache>()); // singleton

        // Composing exercises the factory: create a scope, resolve the scoped composer.
        var result = await cache.GetCurrentAsync(Traffic, Window(), default);
        Assert.NotNull(result);
    }

    [Fact]
    public async Task Materializer_is_registered_as_a_hosted_service()
    {
        await using var sp = BuildProvider();
        Assert.Contains(sp.GetServices<IHostedService>(), h => h is DashboardMaterializerCoordinator);
    }

    /// <summary>
    ///     A caller reacting to an external "data changed" signal (e.g. mae-'s
    ///     GatewayHubInvalidationClient) needs to inject <see cref="DashboardMaterializerCoordinator"/>
    ///     directly to call <c>MarkDirtyAsync</c> -- <c>AddHostedService&lt;T&gt;()</c> alone only
    ///     registers T as an <see cref="IHostedService"/>, not as itself, so a direct
    ///     <c>GetRequiredService&lt;DashboardMaterializerCoordinator&gt;()</c> throws. And whichever
    ///     instance IS resolvable must be the SAME one the host starts, not a second independent
    ///     coordinator with its own tick subscription/state.
    /// </summary>
    [Fact]
    public async Task Coordinator_resolves_directly_and_is_the_same_instance_the_host_starts()
    {
        await using var sp = BuildProvider();

        var direct = sp.GetRequiredService<DashboardMaterializerCoordinator>();
        var hosted = sp.GetServices<IHostedService>().OfType<DashboardMaterializerCoordinator>().Single();

        Assert.Same(direct, hosted);
    }

    // ---------------- fakes ----------------

    private sealed class FakeComposer : IDashboardPageComposer
    {
        public Task<DashboardPageResult> ComposeAsync(
            DashboardPageManifest manifest, DashboardPageWindow window, CancellationToken ct)
            => Task.FromResult(new DashboardPageResult(new DashboardDatasetBundle(null, null, null, null, null)));
    }

    private sealed class FakeCursor : IDashboardChangeCursor
    {
        public long CurrentTick => 1;
        public void Bump(string surface) { }
        public long TickFor(string surface) => 0;
        public IReadOnlyList<string> SurfacesChangedThisTick() => Array.Empty<string>();
    }
}
