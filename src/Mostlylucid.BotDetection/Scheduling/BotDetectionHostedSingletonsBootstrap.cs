using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Definitions.TlsReference;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Markov;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.ThreatIntel;

namespace Mostlylucid.BotDetection.Scheduling;

/// <summary>
///     Tiny bootstrap <see cref="IHostedService"/> whose only job is to RESOLVE
///     the FOSS core's migrated singletons at application start so their
///     constructors fire <see cref="Mostlylucid.Common.Scheduling.IScheduleCoordinator.Subscribe"/> against
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
        _services.GetService<Definitions.WellKnownBots.WellKnownBotRefreshService>();
        _services.GetService<Guardians.GuardianService>();
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
        _services.GetService<BackgroundEnrichmentService>();
        // ThreatIntelEnrichmentService subscribes to Tick10s; the inner queue
        // (ThreatIntelEnrichmentQueue) is its producer side and remains a plain
        // singleton resolved lazily by contributors.
        _services.GetService<ThreatIntelEnrichmentService>();
        // LLM-bound coordinators subscribe to Tick10s; resolved here so the
        // subscriptions are live before the first detection event lands.
        _services.GetService<LlmClassificationCoordinator>();
        _services.GetService<IntentClassificationCoordinator>();
        // BotClusterDescriptionService subscribes to BotClusterService.ClustersUpdated
        // at construction; without eager resolution the SignalR cluster-refresh
        // broadcast + LLM-naming picker push never wire up. This was a pre-existing
        // gap surfaced during the EC6c review; EC6f full-test-sweep will confirm.
        _services.GetService<BotClusterDescriptionService>();
        // SessionPersistenceService subscribes to Tick10s + SessionStore.
        // SessionFinalized; the lifecycle host (separately registered) handles
        // boot init + graceful-shutdown drain.
        _services.GetService<SessionPersistenceService>();
        // Wave 2 Cat-C* paired wave window: all three subscribe to Tick5m so a
        // rollup recompute on this tick sees consistent parent + per-mode state.
        // FingerprintAbsorptionService also wires its ObservationAppended CLR-event
        // fast-path at construction; eager-resolve so the hook is live before the
        // first observation lands.
        _services.GetService<Identity.FingerprintAbsorptionService>();
        _services.GetService<Identity.BrowserModes.FingerprintModeAbsorptionService>();
        _services.GetService<Identity.BrowserModes.FingerprintRollupRecomputeService>();
        // BoundedChannelLearningBus subscribes to Tick1s; HP-mode drains
        // the front-end channel to the inner bus per tick. Resolved via
        // its concrete type because the ILearningEventBus interface
        // forwards to a different code path (the inner bus).
        _services.GetService<BoundedChannelLearningBus>();
        // LearningBackgroundService subscribes to Tick1s; drains the
        // ILearningEventBus reader per tick and dispatches each event
        // to the registered ILearningEventHandlers.
        _services.GetService<LearningBackgroundService>();
        // MeterListenerService is only registered when the dashboard's
        // MonitoringPack.Mode is Local; the GetService probe returns null in
        // every other host so this is the safe place to drive eager resolution.
        _services.GetService<MeterListenerService>();
        // GatewayMeterAccumulator is only registered when the dashboard's
        // MonitoringPack.Mode is GatewayServer; same null-safe pattern.
        _services.GetService<GatewayMeterAccumulator>();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}