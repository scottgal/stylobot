using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Modules;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Services.Llm;
using Mostlylucid.BotDetection.Telemetry;
using Mostlylucid.Atoms.Ephemeral;
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

    /// <summary>Compat alias.</summary>
    public static IServiceCollection AddBotDetectionInMemory(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
        => services.AddBotDetection(configure);

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