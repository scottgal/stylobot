using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     DI wiring for the out-of-request materialization stack. Extracted from
///     <c>AddStyloBotDashboard</c> so it can be resolution-tested in isolation
///     (the singleton-over-scoped-composer factory is the easy thing to get wrong).
/// </summary>
internal static class DashboardMaterializationServiceExtensions
{
    /// <summary>
    ///     Registers the content cache (singleton over the canonical SlidingCacheAtom)
    ///     and the tick-driven materializer hosted service.
    ///
    ///     <para>
    ///         The cache is a SINGLETON (shared across requests + warmed by the
    ///         materializer), so its compose factory resolves the SCOPED
    ///         <see cref="IDashboardPageComposer"/> through a fresh scope per compose — a
    ///         singleton must not capture a scoped dependency. The current tick comes
    ///         from <see cref="IDashboardChangeCursor"/>. Idempotent (TryAdd); the
    ///         materializer self-disables on a viewer-mode host with no
    ///         <c>IScheduleCoordinator</c>.
    ///     </para>
    /// </summary>
    public static IServiceCollection AddDashboardMaterialization(
        this IServiceCollection services,
        bool runTickMaterializer = true)
    {
        services.AddOptions<DashboardMaterializerOptions>()
            .BindConfiguration("BotDetection:Dashboard:Materializer");

        services.TryAddSingleton<IDashboardContentCache>(sp =>
        {
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var cursor = sp.GetRequiredService<IDashboardChangeCursor>();
            var options = sp.GetRequiredService<IOptions<DashboardMaterializerOptions>>();
            return new DashboardContentCache(
                compose: async (manifest, window, ct) =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var composer = scope.ServiceProvider.GetRequiredService<IDashboardPageComposer>();
                    return await composer.ComposeAsync(manifest, window, ct);
                },
                currentTick: () => cursor.CurrentTick,
                options: options);
        });

        // Registered as a singleton (not just AddHostedService<T>, which only exposes T as an
        // IHostedService) so external callers -- e.g. a gateway-push client reacting to an
        // out-of-band "data changed" signal -- can inject DashboardMaterializerCoordinator
        // directly and call MarkDirtyAsync. The AddHostedService factory below resolves that
        // SAME singleton rather than letting the container construct a second, independent
        // instance with its own tick subscription/state.
        services.TryAddSingleton<DashboardMaterializerCoordinator>();
        // Single-UI-gateway stopgap (StyloBotDashboardOptions.IsUiGateway): a non-UI pod
        // keeps the cache + coordinator singleton REGISTERED (TrafficController/
        // VisitorsController require IDashboardContentCache -- a full skip would 500 them)
        // but does not start the tick loop -- no per-replica materialization on pods that
        // only redirect to the elected UI gateway.
        if (runTickMaterializer)
            services.AddHostedService(sp => sp.GetRequiredService<DashboardMaterializerCoordinator>());

        return services;
    }
}
