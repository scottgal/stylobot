using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Stylobot.Gateway.Services;

/// <summary>
///     Tiny bootstrap <see cref="IHostedService"/> whose only job is to RESOLVE
///     the Stylobot.Gateway subproject's migrated singletons at application
///     start so their constructors fire <see cref="Mostlylucid.Common.Scheduling.IScheduleCoordinator.Subscribe"/>
///     against the project-wide coordinator.
///     <para>
///         <b>Why this exists:</b> Wave 2 of the architectural-drift remediation
///         drops services' <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> /
///         <see cref="IHostedService"/> inheritance. They become plain
///         singletons that subscribe to ticks at construction. DI singletons
///         aren't eagerly constructed; a request-time first resolution would
///         leave the subscription dormant. This shim's
///         <see cref="StartAsync"/> forces resolution at boot.
///     </para>
///     <para>
///         Mirrors
///         <c>Mostlylucid.BotDetection.Scheduling.BotDetectionHostedSingletonsBootstrap</c>,
///         <c>Mostlylucid.BotDetection.UI.Services.UiHostedSingletonsBootstrap</c>,
///         and <c>Mostlylucid.BotDetection.ApiHolodeck.Extensions.ApiHolodeckHostedSingletonsBootstrap</c>.
///         Add new gateway-subproject tick subscribers to the resolve list in
///         <see cref="StartAsync"/> as Wave 2 migrates them.
///     </para>
/// </summary>
internal sealed class GatewayHostedSingletonsBootstrap : IHostedService
{
    private readonly IServiceProvider _services;

    public GatewayHostedSingletonsBootstrap(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Force construction so the constructor runs Subscribe(...) against
        // the IScheduleCoordinator. GetService -- not GetRequiredService --
        // because a host that pares the DI graph (test fixture, light
        // sidecar) may not register a particular migrated singleton; the
        // bootstrap should not crash the host when the singleton is absent.
        _services.GetService<ProfileAnalysisWorker>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}