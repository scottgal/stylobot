using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.PrometheusPack.HealthSummaryProviders;
using Mostlylucid.BotDetection.PrometheusPack.Policies;
using Mostlylucid.BotDetection.PrometheusPack.Policies.Triggers;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

namespace Mostlylucid.BotDetection.PrometheusPack.Extensions;

/// <summary>
///     Which ingest mode the Prometheus pack should register.
/// </summary>
/// <remarks>
///     <see cref="Local" /> is the in-gateway mode -- a <c>MeterListener</c>
///     subscribes to process-local meters. <see cref="Remote" /> is the viewer-host
///     mode -- the stream scrapes the gateway's <c>/metrics</c> endpoint over
///     HTTP. The two modes MUST NOT be combined in the same container; the
///     <see cref="PrometheusPackServiceCollectionExtensions.AddPrometheusPack" />
///     extension is the single front door that enforces that invariant.
/// </remarks>
public enum PrometheusPackMode
{
    Local,
    Remote
}

/// <summary>
///     Bound options for <see cref="PrometheusPackServiceCollectionExtensions.AddPrometheusPack" />.
///     The caller picks a <see cref="Mode" /> and supplies the matching sub-callback.
///     Cross-wiring (e.g. <see cref="Mode" /> = <see cref="PrometheusPackMode.Local" />
///     with <see cref="Remote" /> non-null) is rejected up front so configuration
///     mistakes fail loud at composition time.
/// </summary>
public sealed class PrometheusPackOptions
{
    /// <summary>
    ///     Which mode the pack should register. Defaults to
    ///     <see cref="PrometheusPackMode.Local" /> -- the gateway-local mode.
    /// </summary>
    public PrometheusPackMode Mode { get; set; } = PrometheusPackMode.Local;

    /// <summary>
    ///     Optional configuration for <see cref="LocalMeterStreamOptions" />.
    ///     Only valid when <see cref="Mode" /> is <see cref="PrometheusPackMode.Local" />.
    /// </summary>
    public Action<LocalMeterStreamOptions>? Local { get; set; }

    /// <summary>
    ///     Optional configuration for <see cref="RemoteMeterStreamOptions" />.
    ///     Required (and only valid) when <see cref="Mode" /> is
    ///     <see cref="PrometheusPackMode.Remote" /> -- the remote scraper needs
    ///     a <see cref="RemoteMeterStreamOptions.BaseUrl" /> to know which
    ///     gateway to poll.
    /// </summary>
    public Action<RemoteMeterStreamOptions>? Remote { get; set; }
}

/// <summary>
///     DI extensions for the Prometheus pack. Exposes the in-gateway
///     <see cref="LocalMeterStream" /> and the viewer-host
///     <see cref="RemoteMeterStream" /> behind a single static class so each
///     mode has exactly one entry point.
/// </summary>
/// <remarks>
///     <see cref="AddLocalMeterStream" /> and <see cref="AddRemoteMeterStream" />
///     are the building blocks; both stay public for unusual cases (test rigs,
///     bespoke composition roots). The recommended front door is
///     <see cref="AddPrometheusPack" />, which validates mode + config consistency
///     and rejects a second registration in the same container.
/// </remarks>
public static class PrometheusPackServiceCollectionExtensions
{
    /// <summary>
    ///     Single front-door registration for the Prometheus pack. The caller
    ///     fills in <see cref="PrometheusPackOptions" /> to declare which mode
    ///     to register and supplies the matching sub-callback; this method
    ///     routes to <see cref="AddLocalMeterStream" /> or
    ///     <see cref="AddRemoteMeterStream" /> accordingly.
    /// </summary>
    /// <remarks>
    ///     Validation:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 Mode + wrong-side config callback set
    ///                 throws <see cref="InvalidOperationException" /> immediately --
    ///                 picks up Local-mode rigs that accidentally fill in
    ///                 <see cref="PrometheusPackOptions.Remote" /> (or vice versa)
    ///                 instead of silently ignoring the strayed callback.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <see cref="PrometheusPackMode.Remote" /> without a
    ///                 <see cref="PrometheusPackOptions.Remote" /> callback throws --
    ///                 <see cref="RemoteMeterStreamOptions.BaseUrl" /> has no
    ///                 default that points anywhere useful.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 An existing <see cref="IMeterStream" /> registration in
    ///                 the same container throws -- last-write-wins resolution
    ///                 is bug-prone; better to fail loud.
    ///             </description>
    ///         </item>
    ///     </list>
    /// </remarks>
    public static IServiceCollection AddPrometheusPack(
        this IServiceCollection services,
        Action<PrometheusPackOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new PrometheusPackOptions();
        configure(options);

        // mode/config mismatch guard
        if (options.Mode == PrometheusPackMode.Local && options.Remote is not null)
            throw new InvalidOperationException(
                "PrometheusPack: Mode is Local but Remote configuration is set. Pick one mode.");
        if (options.Mode == PrometheusPackMode.Remote && options.Local is not null)
            throw new InvalidOperationException(
                "PrometheusPack: Mode is Remote but Local configuration is set. Pick one mode.");

        // double-registration guard
        if (services.Any(d => d.ServiceType == typeof(IMeterStream)))
            throw new InvalidOperationException(
                "PrometheusPack: an IMeterStream registration already exists. " +
                "Call AddPrometheusPack once per container.");

        switch (options.Mode)
        {
            case PrometheusPackMode.Local:
                services.AddLocalMeterStream(options.Local);
                break;
            case PrometheusPackMode.Remote:
                services.AddRemoteMeterStream(
                    options.Remote
                        ?? throw new InvalidOperationException(
                            "PrometheusPack: Remote mode requires Remote configuration with at least a BaseUrl."));
                break;
            default:
                throw new InvalidOperationException($"PrometheusPack: unknown mode {options.Mode}.");
        }

        // Wave 2 architectural-drift remediation: the migrated singletons need to
        // be CONSTRUCTED at boot so their constructors call Subscribe(...) on the
        // ScheduleCoordinator. PrometheusPackBootstrap is the one IHostedService
        // this pack registers (the coordinator itself is the only other one,
        // owned by Mostlylucid.BotDetection core). It does no periodic work.
        var mode = options.Mode;
        services.AddHostedService(sp => new PrometheusPackBootstrap(sp, mode));

        // Dashboard widget surface -- OWNED by this pack, not the UI assembly.
        // The meter-health tile plugs into the UI's IPackHealthSummaryProvider
        // seam, and MeterHealthFreshnessBootstrap invalidates it on Tick10s via
        // the shared beacon. Everything resolves OPTIONALLY per
        // feedback_remote_mode_optional_di: a host without the dashboard gets no
        // tile and the bootstrap self-disables, so AddPrometheusPack stays valid
        // for dashboard-less gateway-only deployments.
        services.TryAddSingleton<MeterStreamHealthTileCache>();
        services.AddSingleton<IPackHealthSummaryProvider>(sp =>
            new MeterStreamHealthSummaryProvider(
                sp.GetService<IMeterStream>(),
                sp.GetService<MeterStreamHealthTileCache>(),
                sp.GetService<IDashboardLinkResolver>()));
        services.AddHostedService(sp => new MeterHealthFreshnessBootstrap(
            sp.GetService<DashboardFreshnessBeacon>(),
            sp.GetService<IMeterStream>(),
            sp.GetService<MeterStreamHealthTileCache>(),
            sp.GetService<Mostlylucid.Common.Scheduling.IScheduleCoordinator>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<MeterHealthFreshnessBootstrap>>()));

        return services;
    }

    /// <summary>
    ///     Registers the in-gateway MeterListener-backed meter stream
    ///     (LFU summary atoms, signal emission hook). Idempotent default sink.
    ///     Call from the gateway composition root.
    /// </summary>
    public static IServiceCollection AddLocalMeterStream(
        this IServiceCollection services,
        Action<LocalMeterStreamOptions>? configure = null)
    {
        services.AddOptions<LocalMeterStreamOptions>();
        if (configure is not null) services.Configure(configure);

        services.TryAddSingleton<IMeterSignalSink, NullMeterSignalSink>();

        // Self-register IScheduleCoordinator. AddBotDetection registers it on the
        // gateway, but viewer hosts (e.g. the marketing site) call AddPrometheusPack
        // without AddBotDetection -- TryAdd here makes the pack self-sufficient.
        EnsureScheduleCoordinatorRegistered(services);

        // Wave 2: LocalMeterStream is no longer IHostedService. The
        // PrometheusPackBootstrap (registered by AddPrometheusPack) forces
        // construction at boot so the constructor's Subscribe(...) fires.
        // Callers that wire AddLocalMeterStream directly without going through
        // AddPrometheusPack get the same behaviour as long as something
        // resolves IMeterStream early (the dashboard view-component pipeline
        // does this on first SignalR connection).
        //
        // Resolve IScheduleCoordinator via the production constructor (added
        // by Mostlylucid.BotDetection.Extensions.AddBotDetection).
        EnsureMeterDescriptionRegistryRegistered(services);

        services.AddSingleton<LocalMeterStream>(sp => new LocalMeterStream(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalMeterStreamOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalMeterStream>>(),
            sp.GetRequiredService<IMeterSignalSink>(),
            sp.GetRequiredService<Mostlylucid.Common.Scheduling.IScheduleCoordinator>(),
            sp.GetService<MeterDescriptionRegistry>()));
        services.AddSingleton<IMeterStream>(sp => sp.GetRequiredService<LocalMeterStream>());

        AddPolicyMeterIntegration(services);
        return services;
    }

    /// <summary>
    ///     Registers <see cref="RemoteMeterStream" /> as a singleton
    ///     <see cref="IMeterStream" /> + <see cref="Microsoft.Extensions.Hosting.IHostedService" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This MUST NOT be combined with <see cref="AddLocalMeterStream" />
    ///         (which registers <c>LocalMeterStream</c>) or any other
    ///         <see cref="IMeterStream" /> registration in the same container.
    ///         Whichever is registered last wins, which is bug-prone;
    ///         <see cref="AddPrometheusPack" /> is the validated front door.
    ///         This method exists as a building block.
    ///     </para>
    ///     <para>
    ///         <see cref="IMeterSignalSink" /> is registered as
    ///         <see cref="NullMeterSignalSink" /> when no implementation is
    ///         already registered, so the polling loop has a non-null sink.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddRemoteMeterStream(
        this IServiceCollection services,
        Action<RemoteMeterStreamOptions>? configure = null)
    {
        services.AddOptions<RemoteMeterStreamOptions>()
            .BindConfiguration("BotDetection:RemoteMeterStream")
            .Configure(opts => configure?.Invoke(opts));

        services.TryAddSingleton<IMeterSignalSink, NullMeterSignalSink>();

        // Self-register IScheduleCoordinator. AddBotDetection registers it on the
        // gateway, but viewer hosts (e.g. the marketing site) call AddPrometheusPack
        // without AddBotDetection -- TryAdd here makes the pack self-sufficient.
        EnsureScheduleCoordinatorRegistered(services);

        // Wave 6: RemoteMeterStream requires IHttpClientFactory (not optional).
        // Register the named client unconditionally so hosts that haven't called
        // AddHttpClient themselves still get a factory-managed handler pool. The
        // call is idempotent under repeated registration -- the framework's
        // AddHttpClient picks up the existing IHttpClientFactory when one is
        // already registered. Hosts that want custom handlers (retry, circuit
        // breaker, custom certs) can call AddHttpClient(RemoteMeterStream.HttpClientName)
        // themselves and chain handlers before AddRemoteMeterStream runs.
        services.AddHttpClient(RemoteMeterStream.HttpClientName, c =>
            c.DefaultRequestHeaders.UserAgent.ParseAdd(
                Mostlylucid.BotDetection.Identity.StyloBotInternalUserAgent.Value));

        // Wave 2: RemoteMeterStream is no longer IHostedService. The
        // PrometheusPackBootstrap (registered by AddPrometheusPack) forces
        // construction at boot so the constructor's Subscribe(...) fires.
        EnsureMeterDescriptionRegistryRegistered(services);

        services.AddSingleton<RemoteMeterStream>(sp => new RemoteMeterStream(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RemoteMeterStreamOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RemoteMeterStream>>(),
            sp.GetService<IMeterSignalSink>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Mostlylucid.Common.Scheduling.IScheduleCoordinator>(),
            sp.GetService<MeterDescriptionRegistry>()));
        services.AddSingleton<IMeterStream>(sp => sp.GetRequiredService<RemoteMeterStream>());

        AddPolicyMeterIntegration(services);
        return services;
    }

    /// <summary>
    ///     Registers the <see cref="MeterDescriptionRegistry" /> as a singleton and
    ///     seeds it with this assembly. Pack extensions (e.g. AspNetPack, OtelMesh)
    ///     can append their own assembly via
    ///     <see cref="MeterDescriptionRegistryOptions.Assemblies" /> so each pack
    ///     ships descriptions for the meters it owns.
    /// </summary>
    public static IServiceCollection AddMeterDescriptionSourceAssembly(
        this IServiceCollection services, System.Reflection.Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        EnsureMeterDescriptionRegistryRegistered(services);
        services.Configure<MeterDescriptionRegistryOptions>(opt =>
        {
            if (!opt.Assemblies.Contains(assembly)) opt.Assemblies.Add(assembly);
        });
        return services;
    }

    private static void EnsureMeterDescriptionRegistryRegistered(IServiceCollection services)
    {
        services.AddOptions<MeterDescriptionRegistryOptions>()
            .Configure(opt =>
            {
                var thisAssembly = typeof(MeterDescriptionRegistry).Assembly;
                if (!opt.Assemblies.Contains(thisAssembly)) opt.Assemblies.Add(thisAssembly);
            });
        services.TryAddSingleton(sp => new MeterDescriptionRegistry(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<MeterDescriptionRegistryOptions>>().Value,
            sp.GetService<Microsoft.Extensions.Logging.ILogger<MeterDescriptionRegistry>>()));
    }

    /// <summary>
    ///     Wire the Phase F policy + meter bridge:
    ///     <list type="bullet">
    ///         <item>
    ///             <description>
    ///                 <see cref="MeterSnapshotSignalContributor" /> as
    ///                 <see cref="ISignalContributor" /> -- the resolver
    ///                 enumerates it once per request to pump
    ///                 <c>meter.{name}.{facet}</c> into the predicate
    ///                 signal bag.
    ///             </description>
    ///         </item>
    ///         <item>
    ///             <description>
    ///                 <see cref="MeterSignalCatalogSource" /> as
    ///                 <see cref="ISignalCatalogSource" /> -- the catalog
    ///                 enumerates it on read so the editor autocomplete
    ///                 shows live <c>meter.*</c> keys alongside the static
    ///                 SignalKeys vocabulary.
    ///         </description>
    ///         </item>
    ///     </list>
    /// </summary>
    private static void AddPolicyMeterIntegration(IServiceCollection services)
    {
        // Wave 4: MeterSignalsAtom is the canonical singleton that owns the
        // meter-signals snapshot. It subscribes to ScheduleCoordinator.Tick10s
        // in its constructor; PrometheusPackBootstrap resolves it at boot so
        // the subscription fires. The contributor and catalog source both
        // read lock-free from the atom, so the per-request hot path drops
        // from O(N) awaited GetAsync calls to O(N) dictionary copy.
        // Wave 5: register the Options shims with their defaults so the atom +
        // trigger service can resolve IOptions<> uniformly. AddOptions<T>()
        // wires the singleton IOptions<T> without forcing an IConfiguration
        // binding; the defaults set on the Options classes win when nothing
        // is configured. Consumers can layer `services.Configure<T>(...)`
        // on top to override.
        services.AddOptions<MeterSignalsAtomOptions>();
        services.AddOptions<MeterTriggerServiceOptions>();

        services.TryAddSingleton<MeterSignalsAtom>(sp => new MeterSignalsAtom(
            sp.GetRequiredService<IMeterStream>(),
            sp.GetRequiredService<Mostlylucid.Common.Scheduling.IScheduleCoordinator>(),
            sp.GetService<Microsoft.Extensions.Logging.ILogger<MeterSignalsAtom>>(),
            sp.GetService<Microsoft.Extensions.Options.IOptions<MeterSignalsAtomOptions>>()));

        services.TryAddSingleton<MeterSnapshotSignalContributor>();
        services.AddSingleton<ISignalContributor>(sp =>
            sp.GetRequiredService<MeterSnapshotSignalContributor>());

        services.TryAddSingleton<MeterSignalCatalogSource>();
        services.AddSingleton<ISignalCatalogSource>(sp =>
            sp.GetRequiredService<MeterSignalCatalogSource>());

        // Phase G runtime: registry + 1s-tick trigger service. The registry
        // is also TryAdd-registered by the dashboard service extensions, so
        // viewer hosts that include the dashboard but not the gateway still
        // have one. The MeterTriggerService is gateway-only -- it's the loop
        // that actually evaluates trigger rules against the meter snapshot.
        //
        // Wave 2: MeterTriggerService no longer inherits BackgroundService;
        // it subscribes to the ScheduleCoordinator's Tick1s cadence in its
        // constructor. PrometheusPackBootstrap forces the construction at boot.
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Triggers.ArmedRuleRegistry>();
        services.TryAddSingleton<Mostlylucid.BotDetection.PrometheusPack.Policies.Triggers.MeterTriggerService>();
    }

    /// <summary>
    ///     Registers <see cref="Mostlylucid.BotDetection.Scheduling.ScheduleCoordinator" />
    ///     as the canonical tick source unless a "real" coordinator is already
    ///     registered. Mirrors the registration block inside <c>AddBotDetection</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Previously this guard skipped when ANY <see cref="Mostlylucid.Common.Scheduling.IScheduleCoordinator"/>
    ///         was registered. That let
    ///         <see cref="Mostlylucid.Common.Scheduling.NullScheduleCoordinator"/>
    ///         (registered as a defensive default by older versions of
    ///         <c>AddStyloBotDashboard</c>) shadow the real coordinator silently:
    ///         <c>RemoteMeterStream.Subscribe(...)</c> would attach to a no-op
    ///         <c>Subscribe</c>, Tick10s never fired, and the AspNet pack metrics
    ///         tile rendered "0 meters" on every viewer host even though the
    ///         gateway's <c>/metrics</c> was serving <c>aspnet_pack_*</c> families.
    ///     </para>
    ///     <para>
    ///         The new guard:
    ///         <list type="bullet">
    ///             <item><description>Nothing registered -> register real coordinator + IHostedService.</description></item>
    ///             <item><description><see cref="Mostlylucid.Common.Scheduling.NullScheduleCoordinator"/> registered -> remove sentinel, register real.</description></item>
    ///             <item><description>Real <see cref="Mostlylucid.BotDetection.Scheduling.ScheduleCoordinator"/> or a non-sentinel custom impl registered -> no-op (caller wins).</description></item>
    ///         </list>
    ///     </para>
    /// </remarks>
    private static void EnsureScheduleCoordinatorRegistered(IServiceCollection services)
    {
        var existing = services.FirstOrDefault(
            d => d.ServiceType == typeof(Mostlylucid.Common.Scheduling.IScheduleCoordinator));

        if (existing is not null)
        {
            // Real coordinator already registered (or a custom impl) -> caller wins.
            var isNullSentinel =
                ReferenceEquals(existing.ImplementationInstance, Mostlylucid.Common.Scheduling.NullScheduleCoordinator.Instance)
                || existing.ImplementationType == typeof(Mostlylucid.Common.Scheduling.NullScheduleCoordinator);
            if (!isNullSentinel) return;

            // Null sentinel shadowing the real coordinator -- remove it.
            services.Remove(existing);
        }

        services.AddOptions<Mostlylucid.BotDetection.Scheduling.ScheduleCoordinatorOptions>()
            .BindConfiguration(Mostlylucid.BotDetection.Scheduling.ScheduleCoordinatorOptions.SectionName);

        services.AddSingleton<Mostlylucid.BotDetection.Scheduling.ScheduleCoordinator>();
        services.AddSingleton<Mostlylucid.Common.Scheduling.IScheduleCoordinator>(
            sp => sp.GetRequiredService<Mostlylucid.BotDetection.Scheduling.ScheduleCoordinator>());
        services.AddHostedService(
            sp => sp.GetRequiredService<Mostlylucid.BotDetection.Scheduling.ScheduleCoordinator>());
    }
}