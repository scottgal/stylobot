using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Tiny bootstrap <see cref="IHostedService"/> whose only job is to RESOLVE
///     the console's migrated singletons (currently
///     <see cref="HeartbeatService"/>) at application start so their
///     constructors fire
///     <see cref="Scheduling.IScheduleCoordinator.Subscribe"/> against the
///     project-wide coordinator.
///     <para>
///         <b>Why this exists:</b> Wave 2 of the architectural-drift remediation
///         drops console-side services' <see cref="BackgroundService"/> /
///         <see cref="IHostedService"/> inheritance. They become plain
///         singletons that subscribe to ticks at construction. DI singletons
///         aren't eagerly constructed; a request-time first resolution would
///         leave the subscription dormant. This shim's
///         <see cref="StartAsync"/> forces resolution at boot.
///     </para>
///     <para>
///         Mirrors the
///         <c>Mostlylucid.BotDetection.PrometheusPack.Extensions.PrometheusPackBootstrap</c>
///         and <c>Mostlylucid.BotDetection.Commercial.AspNetPack.OtelMeshBootstrap</c>
///         pattern. Add new console-side singletons here as Wave 2 migrates
///         them.
///     </para>
///     <para>
///         <see cref="StopAsync"/> is a no-op; the singletons are disposed via
///         their <see cref="IDisposable"/> contract when the
///         <see cref="IServiceProvider"/> tears down.
///     </para>
/// </summary>
internal sealed class ConsoleHostedSingletonsBootstrap : IHostedService
{
    private readonly IServiceProvider _services;

    public ConsoleHostedSingletonsBootstrap(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Force construction so the constructor runs Subscribe(...) against
        // the IScheduleCoordinator.
        _services.GetService<HeartbeatService>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}