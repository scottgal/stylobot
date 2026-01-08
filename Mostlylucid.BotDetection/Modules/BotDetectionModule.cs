using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Services;
using StyloFlow;
using StyloFlow.Modules;

namespace Mostlylucid.BotDetection.Modules;

/// <summary>
/// StyloFlow module for bot detection.
/// Provides plug-and-play bot detection as a StyloFlow plugin.
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// // Simple registration
/// services.AddStyloFlowModule&lt;BotDetectionModule&gt;();
///
/// // Or with configuration
/// services.AddBotDetection(options => { ... });
/// services.AddStyloFlowModule(new BotDetectionModule());
/// </code>
/// </remarks>
public sealed class BotDetectionModule : IStyloflowWebModule
{
    /// <inheritdoc />
    public string Id => "mostlylucid.botdetection";

    /// <inheritdoc />
    public Version Version => typeof(BotDetectionModule).Assembly.GetName().Version ?? new Version(1, 0, 0);

    /// <inheritdoc />
    public string Name => "Bot Detection";

    /// <inheritdoc />
    public string Description => "Advanced multi-factor bot detection with behavioral analysis, " +
                                  "IP reputation, user agent classification, and machine learning integration.";

    /// <summary>
    /// Features provided by this module.
    /// </summary>
    public static class Features
    {
        public const string Core = "botdetection.core";
        public const string Behavioral = "botdetection.behavioral";
        public const string IpReputation = "botdetection.ip";
        public const string UserAgentAnalysis = "botdetection.useragent";
        public const string MachineLearning = "botdetection.ml";
        public const string Dashboard = "botdetection.dashboard";
        public const string Learning = "botdetection.learning";
    }

    /// <inheritdoc />
    public void ConfigureServices(IServiceCollection services, IStyloflowModuleContext context)
    {
        // Register StyloFlow manifest loading for this assembly
        services.AddStyloFlowFromAssemblies(
            sourceAssemblies: [typeof(BotDetectionModule).Assembly],
            manifestPattern: ".detector.yaml",
            configSectionPath: "BotDetection:Detectors");

        // Register entity types
        services.AddStyloFlowEntitiesFromAssemblies(
            assemblies: [typeof(BotDetectionModule).Assembly],
            pattern: ".entity.yaml");

        // Register core bot detection services
        services.TryAddSingleton<IBotDetectionService, BotDetectionService>();
        services.TryAddSingleton<CommonUserAgentService>();
        services.TryAddSingleton<BrowserVersionService>();
        services.TryAddSingleton<BotListDatabase>();

        // Register the pack-based orchestration
        services.AddBotDetectionPack();

        // Register contributors as detector atoms (adapt existing contributors)
        RegisterContributors(services, context);

        // Register background services
        services.AddHostedService<BotListUpdateService>();

        // Configure options if not already configured
        services.AddOptions<BotDetectionOptions>()
            .BindConfiguration("BotDetection")
            .ValidateDataAnnotations();
    }

    /// <inheritdoc />
    public void MapEndpoints(object endpointRouteBuilder, IStyloflowModuleContext context)
    {
        if (endpointRouteBuilder is not IEndpointRouteBuilder endpoints)
            return;

        var group = endpoints.MapGroup("/api/botdetection")
            .WithTags("BotDetection");

        // Detection status endpoint
        group.MapGet("/status", (HttpContext ctx) =>
        {
            var evidence = ctx.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var ev)
                ? ev as AggregatedEvidence
                : null;
            if (evidence == null)
                return Results.Ok(new { detected = false, message = "No detection run" });

            return Results.Ok(new
            {
                detected = true,
                isBot = evidence.BotProbability > 0.5,
                botProbability = evidence.BotProbability,
                confidence = evidence.Confidence,
                riskBand = evidence.RiskBand.ToString(),
                botType = evidence.PrimaryBotType?.ToString(),
                botName = evidence.PrimaryBotName,
                contributingDetectors = evidence.ContributingDetectors.ToList()
            });
        }).WithName("GetBotDetectionStatus");

        // Detailed detection info (requires elevated access)
        group.MapGet("/details", (HttpContext ctx) =>
        {
            var evidence = ctx.Items.TryGetValue(BotDetectionMiddleware.AggregatedEvidenceKey, out var ev)
                ? ev as AggregatedEvidence
                : null;
            if (evidence == null)
                return Results.NotFound("No detection data available");

            return Results.Ok(new
            {
                botProbability = evidence.BotProbability,
                confidence = evidence.Confidence,
                riskBand = evidence.RiskBand.ToString(),
                earlyExit = evidence.EarlyExit,
                earlyExitVerdict = evidence.EarlyExitVerdict?.ToString(),
                primaryBotType = evidence.PrimaryBotType?.ToString(),
                primaryBotName = evidence.PrimaryBotName,
                processingTimeMs = evidence.TotalProcessingTimeMs,
                categoryBreakdown = evidence.CategoryBreakdown.ToDictionary(
                    kvp => kvp.Key,
                    kvp => new { score = kvp.Value.Score }),
                contributingDetectors = evidence.ContributingDetectors.ToList(),
                failedDetectors = evidence.FailedDetectors.ToList(),
                signals = evidence.Signals
                    .Where(s => !s.Key.Contains("pii", StringComparison.OrdinalIgnoreCase))
                    .ToDictionary(s => s.Key, s => s.Value)
            });
        }).WithName("GetBotDetectionDetails");

        // Health check
        group.MapGet("/health", () => Results.Ok(new
        {
            status = "healthy",
            module = "BotDetection",
            version = typeof(BotDetectionModule).Assembly.GetName().Version?.ToString()
        })).WithName("BotDetectionHealth");
    }

    private static void RegisterContributors(IServiceCollection services, IStyloflowModuleContext context)
    {
        // Register all IContributingDetector implementations
        // These are adapted to work as IDetectorAtom through ContributingDetectorAdapter

        var contributorTypes = typeof(BotDetectionModule).Assembly
            .GetTypes()
            .Where(t => !t.IsAbstract &&
                        !t.IsInterface &&
                        typeof(IContributingDetector).IsAssignableFrom(t));

        foreach (var contributorType in contributorTypes)
        {
            // Register the contributor itself
            services.TryAddSingleton(contributorType);

            // Register as IContributingDetector for discovery
            services.AddSingleton(typeof(IContributingDetector), sp =>
                sp.GetRequiredService(contributorType));
        }
    }
}

/// <summary>
/// Extension methods for easy BotDetection module registration.
/// </summary>
public static class BotDetectionModuleExtensions
{
    /// <summary>
    /// Add the BotDetection module with default configuration.
    /// </summary>
    public static IServiceCollection AddBotDetectionModule(
        this IServiceCollection services,
        IStyloflowModuleContext? context = null)
    {
        return services.AddStyloFlowModule(new BotDetectionModule(), context);
    }

    /// <summary>
    /// Add the BotDetection module with custom options.
    /// </summary>
    public static IServiceCollection AddBotDetectionModule(
        this IServiceCollection services,
        Action<BotDetectionOptions> configureOptions,
        IStyloflowModuleContext? context = null)
    {
        services.Configure(configureOptions);
        return services.AddBotDetectionModule(context);
    }

    /// <summary>
    /// Map BotDetection module endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapBotDetectionEndpoints(
        this IEndpointRouteBuilder endpoints,
        IStyloflowModuleContext? context = null)
    {
        var module = endpoints.ServiceProvider.GetService<BotDetectionModule>()
                     ?? new BotDetectionModule();

        module.MapEndpoints(endpoints, context ?? new StyloflowModuleContext
        {
            ServiceProvider = endpoints.ServiceProvider
        });

        return endpoints;
    }
}
