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
        // The deps the materialization registration consumes:
        services.AddScoped<IDashboardPageComposer, FakeComposer>();   // SCOPED, like the real composer
        services.AddSingleton<IDashboardChangeCursor, FakeCursor>();

        // Code under test — the actual AddStyloBotDashboard wiring, extracted.
        services.AddDashboardMaterialization();

        return services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
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
