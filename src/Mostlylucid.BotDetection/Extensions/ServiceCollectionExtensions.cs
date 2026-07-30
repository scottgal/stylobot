using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Modules;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Services.Llm;
using Mostlylucid.BotDetection.Telemetry;
using Mostlylucid.Ephemeral.Atoms.Llm;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Public DI entry points. All variants funnel through
///     <see cref="BotDetectionModuleExtensions.AddBotDetectionModule"/> which
///     is the atom-orchestrator wire-up. Kept as source-compat shims so
///     existing consumers (Gateway, Demo, Holodeck, StyloExtract,
///     PrometheusPack, tests) continue to compile.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Register the atom-orchestrator detection stack.
    /// </summary>
    public static IServiceCollection AddBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
    {
        if (configure is not null)
            services.Configure(configure);
        return services.AddBotDetectionModule();
    }

    /// <summary>
    ///     Register the atom-orchestrator detection stack, binding from
    ///     configuration.
    /// </summary>
    public static IServiceCollection AddBotDetection(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName = "BotDetection")
    {
        services.Configure<BotDetectionOptions>(configuration.GetSection(sectionName));
        return services.AddBotDetectionModule();
    }

    /// <summary>Compat alias: same shape as <see cref="AddBotDetection(IServiceCollection, Action{BotDetectionOptions}?)"/>.</summary>
    public static IServiceCollection AddSimpleBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
        => services.AddBotDetection(configure);

    /// <summary>Compat alias.</summary>
    public static IServiceCollection AddComprehensiveBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
        => services.AddBotDetection(configure);

    /// <summary>Compat alias.</summary>
    public static IServiceCollection AddAdvancedBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
        => services.AddBotDetection(configure);

    /// <summary>
    ///     Ephemeral mode (integration tests, CI, the gateway economy flag): the full
    ///     detection pipeline with zero SQLite files on disk. Identity, session learning,
    ///     and weight learning silently degrade; per-request detection runs unchanged.
    /// </summary>
    /// <remarks>
    ///     Three mechanisms cover the SQLite surface of
    ///     <see cref="AddBotDetection(IServiceCollection, Action{BotDetectionOptions}?)"/>:
    ///     <list type="bullet">
    ///         <item><c>Identity.Enabled = false</c> parks the identity layer
    ///         (fingerprints.db + browser-mode / pool-collision / vec stores), which opens
    ///         its own files directly and can only degrade without persistence.</item>
    ///         <item>An empty <c>DatabasePath</c> routes the shared
    ///         <c>Func&lt;DbConnection&gt;</c> factory (CentroidSequenceStore, AssetHashStore)
    ///         to <c>file::memory:</c> — no file.</item>
    ///         <item>The stores that build their own connection string from
    ///         <c>DatabasePath</c> (and so ignore an empty value) are swapped for their
    ///         Null / InMemory variants below.</item>
    ///     </list>
    ///     Challenge / approval / and path-lifecycle stores already default to
    ///     Null / InMemory in the module, so they need no swap. Centroid stores
    ///     are now wired as real SQLite stores by default (Task C); the three
    ///     interface bindings are swapped to Null variants below so Slim* and
    ///     SessionVectorWarmupService see no-op stores in ephemeral mode.
    ///     <para>
    ///     Lenient pattern (same as PoolCollisionStore): only the interface
    ///     bindings are replaced; the concrete stores and their
    ///     StoreInitService&lt;T&gt; hosted services remain in the container but
    ///     are never reached via the interface paths that matter for detection.
    ///     In a full host these concrete stores will still create their own
    ///     *.db files at startup -- a known-acceptable gap mirrored by
    ///     SqlitePoolCollisionStore's treatment.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddBotDetectionInMemory(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
    {
        services.AddBotDetection(options =>
        {
            options.Identity.Enabled = false;
            options.DatabasePath = string.Empty;
            configure?.Invoke(options);
        });

        // Replace() swaps the TryAdd-registered SQLite descriptor; the concrete SQLite
        // types stay in the container (some are resolved by name) but no I/O flows
        // through these interface bindings.
        services.Replace(ServiceDescriptor.Singleton<Data.IWeightStore, Data.NullWeightStore>());
        services.Replace(ServiceDescriptor.Singleton<Data.IDetectionArchive, Data.NullDetectionArchive>());
        // Bot signature catalog: the SQLite BotListDatabase writes patterns +
        // datacenter-IP ranges to botdetection.db. In-memory holds both lists from the
        // same IBotListFetcher source with no file.
        services.Replace(ServiceDescriptor.Singleton<Data.IBotListDatabase>(sp =>
            new Data.InMemoryBotListDatabase(
                sp.GetRequiredService<Data.IBotListFetcher>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Data.InMemoryBotListDatabase>>())));
        // Centroid stores: the module now wires real SQLite WriteBehindLfuStore stores
        // as the FOSS default (Task C). Swap back to Null variants here so Slim* and
        // SessionVectorWarmupService receive no-op stores in ephemeral mode and no
        // centroid *.db files are touched by detection paths. The concrete stores and
        // their StoreInitService<T> hosted services remain in DI (lenient pattern).
        services.Replace(ServiceDescriptor.Singleton<Data.Contracts.ISignatureCentroidStore, Data.NullSignatureCentroidStore>());
        services.Replace(ServiceDescriptor.Singleton<Data.Contracts.ISessionCentroidStore, Data.NullSessionCentroidStore>());
        services.Replace(ServiceDescriptor.Singleton<Data.Contracts.IIntentCentroidStore, Data.NullIntentCentroidStore>());
        // Fingerprint store: even with Identity disabled, the mode-centroid classifier reads
        // catalogue centroids via IFingerprintStore.GetByCatalogueKindAsync (ModeCentroidCatalogue),
        // which otherwise resolves the concrete SqliteFingerprintStore and creates fingerprints.db.
        // Swap to the null store so ephemeral mode touches no identity db file. Identity learning is
        // already parked here; this closes the classifier read path any catalogue (e.g. browser_char)
        // would hit.
        services.Replace(ServiceDescriptor.Singleton<Identity.IFingerprintStore, Identity.NullFingerprintStore>());
        // Webhook endpoint reputation: the SQLite store derives its db path from
        // DatabasePath's directory, which is meaningless once DatabasePath is emptied
        // above. Swap to the null store so ephemeral mode creates no webhooks.db file;
        // WebhookSensor already treats a missing reputation source as "no IP
        // corroborator available" (shape-only, never-corroborated) via its optional ctor
        // param, so detection degrades gracefully rather than throwing.
        services.Replace(ServiceDescriptor.Singleton<Reputation.IWebhookEndpointReputation,
            Reputation.NullWebhookEndpointReputation>());

        return services;
    }

    /// <summary>
    ///     Configure bot detection options (post-registration customization).
    /// </summary>
    public static IServiceCollection ConfigureBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions> configure)
    {
        services.Configure(configure);
        return services;
    }

    /// <summary>
    ///     Register non-orchestrator setup services (kept as an empty extension
    ///     point for hosts that historically called this before / after
    ///     <see cref="AddBotDetection(IServiceCollection, Action{BotDetectionOptions}?)"/>).
    /// </summary>
    public static IServiceCollection AddBotDetectionSetupServices(this IServiceCollection services)
        => services;

    /// <summary>
    ///     Registers <see cref="BotDetectionSignalMeter"/> and
    ///     <see cref="BotDetectionInstrumentation"/> for OpenTelemetry.
    /// </summary>
    public static IServiceCollection AddBotDetectionTelemetry(
        this IServiceCollection services,
        Action<BotDetectionTelemetryOptions>? configure = null)
    {
        services.AddOptions<BotDetectionTelemetryOptions>()
            .BindConfiguration("BotDetection:Telemetry")
            .Configure(opts => configure?.Invoke(opts));

        services.TryAddSingleton<BotDetectionSignalMeter>();
        services.TryAddSingleton<BotDetectionInstrumentation>();
        services.TryAddSingleton<IActiveUpstreamProbeState, ActiveUpstreamProbeState>();

        return services;
    }

    /// <summary>
    ///     Registers the per-fingerprint LLM-naming pipeline.
    /// </summary>
    public static IServiceCollection AddFingerprintLlmNamer(this IServiceCollection s) =>
        s.AddSingleton<FingerprintInFlightSet>()
         .AddSingleton<DriftTriggeredFingerprintPicker>()
         .AddSingleton<IEphemeralPicker<FingerprintPickItem>>(sp => sp.GetRequiredService<DriftTriggeredFingerprintPicker>())
         .AddSingleton<IEphemeralPrompter<FingerprintPickItem>, FingerprintNamingPrompter>()
         .AddSingleton<IEphemeralLlmInvoker<FingerprintNamingResult>, FingerprintLlmInvoker>()
         .AddSingleton<IEphemeralWriteback<FingerprintPickItem, FingerprintNamingResult>, FingerprintLlmWriteback>()
         .AddEphemeralLlmCoordinator<FingerprintPickItem, FingerprintNamingResult>(opts =>
         {
             opts.Cadence = TickCadence.Tick1m;
             opts.MaxItemsPerTick = 10;
             opts.MaxConcurrent = Math.Max(1, Environment.ProcessorCount / 2);
             opts.SubscriberName = "FingerprintLlmNamer";
             opts.InvocationTimeout = TimeSpan.FromSeconds(30);
         });

    /// <summary>
    ///     Registers the cluster LLM-naming pipeline.
    /// </summary>
    public static IServiceCollection AddClusterLlmNamer(this IServiceCollection s) =>
        s.AddSingleton<ClusterInFlightSet>()
         .AddSingleton<NeedsDescriptionClusterPicker>()
         .AddSingleton<IEphemeralPicker<ClusterPickItem>>(sp => sp.GetRequiredService<NeedsDescriptionClusterPicker>())
         .AddSingleton<IEphemeralPrompter<ClusterPickItem>, ClusterNamingPrompter>()
         .AddSingleton<IEphemeralLlmInvoker<ClusterNamingResult>, ClusterLlmInvoker>()
         .AddSingleton<IEphemeralWriteback<ClusterPickItem, ClusterNamingResult>, ClusterLlmWriteback>()
         .AddEphemeralLlmCoordinator<ClusterPickItem, ClusterNamingResult>(opts =>
         {
             opts.Cadence = TickCadence.Tick5m;
             opts.MaxItemsPerTick = 4;
             opts.MaxConcurrent = 2;
             opts.SubscriberName = "ClusterLlmNamer";
             opts.InvocationTimeout = TimeSpan.FromSeconds(60);
         });
}