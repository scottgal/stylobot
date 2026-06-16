using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.ApiHolodeck.Services;

namespace Mostlylucid.BotDetection.ApiHolodeck.Extensions;

/// <summary>
///     Tiny bootstrap <see cref="IHostedService"/> whose only job is to RESOLVE
///     the ApiHolodeck migrated singletons (currently <see cref="HoneypotReporter"/>)
///     at application start so their constructors fire
///     <see cref="Scheduling.IScheduleCoordinator.Subscribe"/> against the
///     project-wide coordinator.
///
///     <para>
///         <b>Why this exists:</b> Wave 2 of the architectural-drift remediation
///         dropped <see cref="HoneypotReporter"/>'s
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> inheritance.
///         It is now a plain singleton that subscribes to ticks at construction.
///         But DI singletons are not eagerly constructed; a request-time first
///         resolution would leave the subscription dormant until something
///         resolved the type. This shim's <see cref="StartAsync"/> forces
///         resolution at boot.
///     </para>
///     <para>
///         Mirrors <c>PrometheusPackBootstrap</c> (FOSS) and
///         <c>OtelMeshBootstrap</c> (commercial). Per the Wave 2 migration plan,
///         the same shim resolves N migrated singletons -- additional ApiHolodeck
///         migrations land here rather than spinning a new shim per service.
///     </para>
///     <para>
///         <see cref="StopAsync"/> is a no-op; the singletons are disposed via
///         their <see cref="IDisposable"/> contract when the
///         <see cref="IServiceProvider"/> tears down.
///     </para>
/// </summary>
internal sealed class ApiHolodeckHostedSingletonsBootstrap : IHostedService
{
    private readonly IServiceProvider _services;

    public ApiHolodeckHostedSingletonsBootstrap(IServiceProvider services)
    {
        _services = services;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Force construction of the migrated singleton so its constructor runs
        // Subscribe(...) against the IScheduleCoordinator.
        _services.GetService<HoneypotReporter>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}