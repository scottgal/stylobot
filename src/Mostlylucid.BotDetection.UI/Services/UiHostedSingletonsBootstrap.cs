using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Tiny bootstrap <see cref="IHostedService"/> whose only job is to RESOLVE
///     the UI assembly's migrated singletons at application start so their
///     constructors fire <see cref="Mostlylucid.Common.Scheduling.IScheduleCoordinator.Subscribe"/>
///     against the project-wide coordinator.
///     <para>
///         Mirrors the
///         <c>Mostlylucid.BotDetection.Scheduling.BotDetectionHostedSingletonsBootstrap</c>,
///         <c>Mostlylucid.BotDetection.PrometheusPack.Extensions.PrometheusPackBootstrap</c>,
///         and <c>Mostlylucid.BotDetection.Console.Services.ConsoleHostedSingletonsBootstrap</c>
///         patterns. Add new UI-assembly tick subscribers to the resolve list in
///         <see cref="StartAsync"/> as Wave 2 migrates them.
///     </para>
/// </summary>
internal sealed class UiHostedSingletonsBootstrap : IHostedService
{
    private readonly IServiceProvider _services;

    public UiHostedSingletonsBootstrap(IServiceProvider services)
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
        _services.GetService<DashboardSummaryBroadcaster>();
        _services.GetService<RemoteMetricCollector>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}