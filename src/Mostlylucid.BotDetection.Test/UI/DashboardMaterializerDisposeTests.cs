using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Host-teardown contract for <see cref="DashboardMaterializerCoordinator"/>: the
///     container may dispose it more than once, and that must be safe.
///     <para>
///         Root cause this pins: <c>AddDashboardMaterialization</c> registers the
///         coordinator TWICE over the same instance — <c>TryAddSingleton&lt;DashboardMaterializerCoordinator&gt;()</c>
///         (so callers can inject it and call <c>MarkDirtyAsync</c>) plus
///         <c>AddHostedService(sp =&gt; sp.GetRequiredService&lt;DashboardMaterializerCoordinator&gt;())</c>.
///         The DI container captures the disposable once per registration call site, so on
///         <c>ServiceProvider.Dispose()</c> it calls <c>Dispose()</c> twice; the second call
///         ran <c>_watchdogCts.Cancel()</c> on an already-disposed CTS and threw
///         <c>ObjectDisposedException: The CancellationTokenSource has been disposed</c> during
///         host teardown.
///     </para>
/// </summary>
public sealed class DashboardMaterializerDisposeTests
{
    private static ServiceProvider Build(bool runTickMaterializer)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
        services.AddSingleton<IDashboardChangeCursor>(new DashboardChangeCursor());
        services.AddSingleton<IDashboardPageManifestSource>(new DefaultDashboardPageManifestSource());
        // DashboardCacheDiskPersistence (the second hosted service AddDashboardMaterialization
        // registers) needs the shingle cache; without it, resolving the IHostedService
        // enumerable throws before teardown and the double-dispose never gets exercised.
        services.AddSingleton<DashboardWidgetShingleCache>();
        services.AddDashboardMaterialization(runTickMaterializer);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task Container_teardown_is_idempotent_even_though_the_coordinator_is_registered_twice()
    {
        await using var provider = Build(runTickMaterializer: true);

        // Resolve BOTH registrations over the same instance — this is what the container
        // does at teardown: the singleton call site and the IHostedService factory call site.
        var coordinator = provider.GetRequiredService<DashboardMaterializerCoordinator>();
        var hosted = provider.GetServices<IHostedService>().ToList();
        Assert.Contains(coordinator, hosted);

        // Host teardown: StopAsync first (HostedServiceExecutor), then container dispose.
        // DisposeAsync is the host's shape — DashboardContentCache is async-dispose-only.
        var ex = await Record.ExceptionAsync(async () =>
        {
            await coordinator.StopAsync(CancellationToken.None);
            await provider.DisposeAsync();
        });

        Assert.Null(ex);
    }

    [Fact]
    public async Task Dispose_called_twice_directly_does_not_throw()
    {
        var provider = Build(runTickMaterializer: true);
        var coordinator = provider.GetRequiredService<DashboardMaterializerCoordinator>();

        coordinator.Dispose();

        var ex = Record.Exception(() => coordinator.Dispose());
        Assert.Null(ex);
        await provider.DisposeAsync();
    }

    [Fact]
    public async Task StopAsync_after_Dispose_does_not_throw()
    {
        // Defensive ordering: a host that disposes the container before stopping hosted
        // services (or a test double doing so) must not crash teardown.
        var provider = Build(runTickMaterializer: true);
        var coordinator = provider.GetRequiredService<DashboardMaterializerCoordinator>();

        coordinator.Dispose();

        var ex = await Record.ExceptionAsync(() => coordinator.StopAsync(CancellationToken.None));
        Assert.Null(ex);
        await provider.DisposeAsync();
    }
}
