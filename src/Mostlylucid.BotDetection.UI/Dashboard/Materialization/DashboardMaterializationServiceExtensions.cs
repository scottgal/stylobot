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
    public static IServiceCollection AddDashboardMaterialization(this IServiceCollection services)
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

        // REMOTE-MODE GUARD. On a remote-mode dashboard host (marketing site, stylobot-ui)
        // the tick loop must not run. Prod ran 3 website replicas with no session affinity,
        // each with its OWN materializer, its own tick timing and its own warm state, so a
        // request round-robined into whichever pod happened to be warm: Top Bots counts
        // disagreeing with rows, charts emptying on refresh, the traffic page not updating.
        // One cause, four symptoms, and structurally random rather than coincidental.
        //
        // This mirrors the SignatureAggregateCacheWarmupService guard in
        // StyloBotDashboardServiceExtensions -- same DashboardSourceOptions probe, and
        // deliberately the same SHAPE: that guard skips only the HOSTED SERVICE and says so
        // ("the cache stays registered (satisfies DI for middleware / view-components) but
        // stays empty, so all read paths cleanly fall through to IDashboardEventStore").
        //
        // Guarding the whole AddDashboardMaterialization() call instead would ALSO drop
        // IDashboardContentCache, and that is not safe: TrafficController (:43) and
        // VisitorsController (:35) take it as a REQUIRED, non-nullable ctor parameter, so
        // /dashboard/traffic and /dashboard/visitors -- two of the pages in the symptom list
        // -- would 500 on every request. Middleware and view components tolerate its absence
        // (nullable fields / GetService); those two controllers do not.
        //
        // The coordinator singleton above stays registered so out-of-band callers can still
        // inject it for MarkDirtyAsync; it simply never ticks here.
        //
        // NOTE: the class doc's claim that the materializer "self-disables on a viewer-mode
        // host with no IScheduleCoordinator" does NOT hold in practice -- AddStyloBotDashboard
        // unconditionally TryAddSingletons an IScheduleCoordinator a few lines before calling
        // this method, so the dependency is always present and the self-disable never fires.
        // That is why this needs an explicit registration-time guard.
        var isRemoteModeHost = services.Any(
            s => s.ServiceType == typeof(Mostlylucid.BotDetection.UI.Adapters.Remote.DashboardSourceOptions));
        if (!isRemoteModeHost)
        {
            services.AddHostedService(sp => sp.GetRequiredService<DashboardMaterializerCoordinator>());
        }

        return services;
    }
}
