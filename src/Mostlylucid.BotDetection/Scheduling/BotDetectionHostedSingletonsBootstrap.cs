using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Definitions.TlsReference;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Scheduling;

/// <summary>
///     Tiny bootstrap <see cref="IHostedService"/> whose only job is to RESOLVE
///     the FOSS core's migrated singletons at application start so their
///     constructors fire <see cref="IScheduleCoordinator.Subscribe"/> against
///     the project-wide coordinator.
///     <para>
///         <b>Why this exists:</b> Wave 2 of the architectural-drift remediation
///         drops services' <see cref="BackgroundService"/> /
///         <see cref="IHostedService"/> inheritance. They become plain
///         singletons that subscribe to ticks at construction. DI singletons
///         aren't eagerly constructed; a request-time first resolution would
///         leave the subscription dormant. This shim's
///         <see cref="StartAsync"/> forces resolution at boot.
///     </para>
///     <para>
///         Mirrors the
///         <c>Mostlylucid.BotDetection.PrometheusPack.Extensions.PrometheusPackBootstrap</c>,
///         <c>Mostlylucid.BotDetection.Console.Services.ConsoleHostedSingletonsBootstrap</c>,
///         and <c>Mostlylucid.BotDetection.Commercial.AspNetPack.OtelMeshBootstrap</c>
///         patterns. Add new core-assembly tick subscribers to the resolve list
///         in <see cref="StartAsync"/> as Wave 2 migrates them.
///     </para>
///     <para>
///         <see cref="StopAsync"/> is a no-op; the singletons are disposed via
///         their <see cref="IDisposable"/> contract when the
///         <see cref="IServiceProvider"/> tears down.
///     </para>
/// </summary>
internal sealed class BotDetectionHostedSingletonsBootstrap : IHostedService
{
    private readonly IServiceProvider _services;

    public BotDetectionHostedSingletonsBootstrap(IServiceProvider services)
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
        _services.GetService<DeploymentNormCalibrationService>();
        _services.GetService<LicenseStateRefreshService>();
        // CommonUserAgentService is resolved via its interface (the
        // production registration is TryAddSingleton<ICommonUserAgentService,
        // CommonUserAgentService>); GetService<concrete> would miss it.
        _services.GetService<ICommonUserAgentService>();
        _services.GetService<Ja3CorpusRefreshService>();
        _services.GetService<VerifiedBotRegistry>();
        _services.GetService<EntityResolutionService>();
        _services.GetService<SessionAtomizerService>();
        _services.GetService<VectorCompactionService>();
        _services.GetService<FingerprintDriftService>();
        _services.GetService<PopulationMarkovService>();
        _services.GetService<IdentityGlobalWeightsCache>();
        _services.GetService<IdentityWeightCalibrationService>();
        _services.GetService<SignatureConvergenceService>();
        _services.GetService<BotListUpdateService>();
        // MeterListenerService is only registered when the dashboard's
        // MonitoringPack.Mode is Local; the GetService probe returns null in
        // every other host so this is the safe place to drive eager resolution.
        _services.GetService<MeterListenerService>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}