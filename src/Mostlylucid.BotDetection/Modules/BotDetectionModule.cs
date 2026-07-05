using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Learning;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using StyloFlow;
using StyloFlow.Modules;
using StyloFlow.Orchestration;

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

        // Register the atom-orchestrator
        services.AddBotDetectionOrchestrator();

        // Register contributors as detector atoms (adapt existing contributors)
        RegisterContributors(services, context);

        // Register background services
        // Wave 2: BotListUpdateService migrated to ScheduleCoordinator tick.1h.
        // Eager-resolved by BotDetectionHostedSingletonsBootstrap so the
        // constructor's Subscribe(...) fires at boot.
        services.AddSingleton<BotListUpdateService>();
        // Task-#65 reference implementation: BotListUpdateService raises a
        // notification signal after every successful refresh so consumers
        // can react (subscribe to TypedSignalRaised) instead of polling
        // IBotListDatabase. Retention window is short -- the notification
        // is stateless, the database write is authoritative.
        services.TryAddSingleton<Mostlylucid.Ephemeral.TypedSignalSink<BotListUpdatedSignal>>(sp =>
        {
            var inner = new Mostlylucid.Ephemeral.SignalSink(maxCapacity: 32, maxAge: TimeSpan.FromMinutes(15));
            return new Mostlylucid.Ephemeral.TypedSignalSink<BotListUpdatedSignal>(
                inner, maxCapacity: 32, maxAge: TimeSpan.FromMinutes(15));
        });

        // Learning fabric.
        //
        // The shared TypedSignalSink<LearningEvent> is registered as a
        // singleton independently of the coordinator: it is the boot-time
        // transport escalators raise into, so it must exist before any
        // coordinator or dispatcher does. The sink's first raise fires the
        // init signal on IInitSignalBus, which StyloFlow's InitSignalBootstrap
        // observes to lazy-construct the coordinator + dispatcher. Zero cost
        // until the first hot-path escalator write lands.
        services.AddInitSignalBus();
        services.AddOptions<LearningSignalSinkOptions>()
            .BindConfiguration(LearningSignalSinkOptions.SectionName);
        services.TryAddSingleton<Mostlylucid.Ephemeral.TypedSignalSink<Events.LearningEvent>>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<LearningSignalSinkOptions>>().Value;
            var bus = sp.GetRequiredService<StyloFlow.Orchestration.IInitSignalBus>();
            var inner = new Mostlylucid.Ephemeral.SignalSink(opts.Capacity, opts.Retention);
            var sink = new Mostlylucid.Ephemeral.TypedSignalSink<Events.LearningEvent>(
                inner, maxCapacity: opts.Capacity, maxAge: opts.Retention);
            var initFired = 0;
            sink.TypedSignalRaised += _ =>
            {
                // First raise fires the init signal exactly once; subsequent
                // raises no-op the CompareExchange. Bus.Raise is itself
                // idempotent as a second layer of safety.
                if (System.Threading.Interlocked.Exchange(ref initFired, 1) == 0)
                    bus.Raise(LearningSignalSinkOptions.InitSignal);
            };
            return sink;
        });
        // ILearningCoordinator: registered first (interface → impl mapping)
        // so the AddOnInitSignal<ILearningCoordinator> TryAdd inside the
        // helper is a no-op; the bootstrap resolves via the existing mapping.
        services.TryAddSingleton<ILearningCoordinator, LearningCoordinator>();
        services.AddOnInitSignal<ILearningCoordinator>(LearningSignalSinkOptions.InitSignal);
        services.AddOnInitSignal<LearningBackgroundService>(LearningSignalSinkOptions.InitSignal);

        // Session store: shared per-domain, priority-shaped eviction, boot-time.
        // Escalators upsert SessionSample → aggregate; SessionAtom observes
        // Changes and emits persistence signals on fingerprint shift.
        // See reference_session_layer_and_fingerprint_levels for the model.
        services.AddOptions<Orchestration.Sessions.SessionStoreOptions>()
            .BindConfiguration(Orchestration.Sessions.SessionStoreOptions.SectionName);
        services.AddOptions<Orchestration.Sessions.SessionAtomOptions>()
            .BindConfiguration(Orchestration.Sessions.SessionAtomOptions.SectionName);
        services.TryAddSingleton<Orchestration.Sessions.SessionStore>();
        services.AddOnInitSignal<Orchestration.Sessions.SessionAtom>(
            Orchestration.Sessions.SessionStoreOptions.InitSignal);
        services.AddOnInitSignal<Orchestration.Sessions.SessionPersistenceAtom>(
            Orchestration.Sessions.SessionStoreOptions.InitSignal);

        // LLM classification lane: shared request sink fronts the
        // coordinator's bounded channel. Escalators raise onto the sink;
        // the coordinator lazy-boots on the first raise via AddOnInitSignal.
        // The internal channel still throttles LLM calls -- the sink is
        // purely the fan-in / init-trigger surface.
        services.AddOptions<Services.LlmClassificationSinkOptions>()
            .BindConfiguration(Services.LlmClassificationSinkOptions.SectionName);
        services.TryAddSingleton<Mostlylucid.Ephemeral.TypedSignalSink<Services.LlmClassificationRequest>>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Services.LlmClassificationSinkOptions>>().Value;
            var bus = sp.GetRequiredService<StyloFlow.Orchestration.IInitSignalBus>();
            var inner = new Mostlylucid.Ephemeral.SignalSink(opts.Capacity, opts.Retention);
            var sink = new Mostlylucid.Ephemeral.TypedSignalSink<Services.LlmClassificationRequest>(
                inner, maxCapacity: opts.Capacity, maxAge: opts.Retention);
            var initFired = 0;
            sink.TypedSignalRaised += _ =>
            {
                if (System.Threading.Interlocked.Exchange(ref initFired, 1) == 0)
                    bus.Raise(Services.LlmClassificationSinkOptions.InitSignal);
            };
            return sink;
        });
        services.AddOnInitSignal<Services.LlmClassificationCoordinator>(
            Services.LlmClassificationSinkOptions.InitSignal);

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
        // Intentionally empty. Legacy IContributingDetector implementations
        // must be rewritten as native IDetectorAtom implementations against
        // the Ephemeral taxonomy roles (SensorAtom / ExtractorAtom /
        // ProposerAtom / ConstrainerAtom / RankerAtom / RendererAtom /
        // CoordinatorAtom / FeedbackAtom / EscalatorAtom / GuardAtom).
        //
        // ContributingDetectorAdapter was a bridge that encoded the OLD
        // BlackboardState-shaped detector contract as an IDetectorAtom. The
        // operator has ruled that out ("NO ADAPTORS FIX THEM") -- the adapter
        // preserves the wrong shape rather than migrating to atoms. The
        // migration is: rewrite each contributor as a native IDetectorAtom
        // whose DetectAsync reads/writes the SignalSink (blackboard) directly,
        // not HttpContext.Items via BlackboardState.
        //
        // Native atoms register themselves via AddDetectorAtom<T>() (see
        // BotDetectionOrchestrator.cs BotDetectionOrchestratorExtensions) as new
        // IContributingDetector implementations are converted.
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
