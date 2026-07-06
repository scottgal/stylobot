using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Modules;
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
    ///     Challenge / approval / path-lifecycle / centroid stores already default to
    ///     Null / InMemory in the module, so they need no swap.
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