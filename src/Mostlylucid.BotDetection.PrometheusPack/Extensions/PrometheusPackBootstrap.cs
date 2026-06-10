using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.PrometheusPack.Policies.Triggers;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

namespace Mostlylucid.BotDetection.PrometheusPack.Extensions;

/// <summary>
///     Tiny bootstrap <see cref="IHostedService"/> whose only job is to RESOLVE
///     the migrated singletons (<see cref="LocalMeterStream"/>,
///     <see cref="RemoteMeterStream"/>, <see cref="MeterTriggerService"/>) at
///     application start so their constructors fire
///     <see cref="Scheduling.IScheduleCoordinator.Subscribe"/> against the
///     project-wide coordinator.
///
///     <para>
///         <b>Why this exists:</b> Wave 2 of the architectural-drift remediation
///         dropped these services' <see cref="BackgroundService"/> /
///         <see cref="IHostedService"/> inheritance. They're now plain singletons
///         that subscribe to ticks at construction. But DI singletons aren't
///         eagerly constructed; a request-time first resolution would leave the
///         subscription dormant until something resolved the type. This shim's
///         <see cref="StartAsync"/> forces resolution at boot.
///     </para>
///     <para>
///         The pack registers ONE bootstrap shim that resolves N migrated
///         singletons. Per the migration spec, this IS the second
///         <see cref="IHostedService"/> in the pack (the first is
///         <see cref="Scheduling.ScheduleCoordinator"/> itself, registered by the
///         FOSS core DI) -- but it does no periodic work. Once it has resolved
///         the singletons it returns immediately.
///     </para>
///     <para>
///         <see cref="StopAsync"/> is a no-op; the singletons are disposed via
///         their <see cref="IDisposable"/> contract when the
///         <see cref="IServiceProvider"/> tears down.
///     </para>
/// </summary>
internal sealed class PrometheusPackBootstrap : IHostedService
{
    private readonly IServiceProvider _services;
    private readonly PrometheusPackMode _mode;

    public PrometheusPackBootstrap(IServiceProvider services, PrometheusPackMode mode)
    {
        _services = services;
        _mode = mode;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Force construction of the mode-specific stream so its constructor
        // runs Subscribe(...) against the IScheduleCoordinator.
        switch (_mode)
        {
            case PrometheusPackMode.Local:
                _services.GetService<LocalMeterStream>();
                break;
            case PrometheusPackMode.Remote:
                _services.GetService<RemoteMeterStream>();
                break;
        }

        // The trigger service is registered via AddPolicyMeterIntegration in
        // both modes. Resolve it unconditionally; the resolution is a no-op if
        // no MeterSnapshotSignalContributor is wired.
        _services.GetService<MeterTriggerService>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}