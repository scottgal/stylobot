using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Policies.Signals;
using Mostlylucid.BotDetection.PrometheusPack.Policies;
using Mostlylucid.BotDetection.PrometheusPack.Policies.Triggers;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

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
        services.AddSingleton<LocalMeterStream>(sp => new LocalMeterStream(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LocalMeterStreamOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<LocalMeterStream>>(),
            sp.GetRequiredService<IMeterSignalSink>(),
            sp.GetRequiredService<Mostlylucid.BotDetection.Scheduling.IScheduleCoordinator>()));
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

        // Wave 6: RemoteMeterStream requires IHttpClientFactory (not optional).
        // Register the named client unconditionally so hosts that haven't called
        // AddHttpClient themselves still get a factory-managed handler pool. The
        // call is idempotent under repeated registration -- the framework's
        // AddHttpClient picks up the existing IHttpClientFactory when one is
        // already registered. Hosts that want custom handlers (retry, circuit
        // breaker, custom certs) can call AddHttpClient(RemoteMeterStream.HttpClientName)
        // themselves and chain handlers before AddRemoteMeterStream runs.
        services.AddHttpClient(RemoteMeterStream.HttpClientName);

        // Wave 2: RemoteMeterStream is no longer IHostedService. The
        // PrometheusPackBootstrap (registered by AddPrometheusPack) forces
        // construction at boot so the constructor's Subscribe(...) fires.
        services.AddSingleton<RemoteMeterStream>(sp => new RemoteMeterStream(
            sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<RemoteMeterStreamOptions>>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RemoteMeterStream>>(),
            sp.GetService<IMeterSignalSink>(),
            sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<Mostlylucid.BotDetection.Scheduling.IScheduleCoordinator>()));
        services.AddSingleton<IMeterStream>(sp => sp.GetRequiredService<RemoteMeterStream>());

        AddPolicyMeterIntegration(services);
        return services;
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
            sp.GetRequiredService<Mostlylucid.BotDetection.Scheduling.IScheduleCoordinator>(),
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
}