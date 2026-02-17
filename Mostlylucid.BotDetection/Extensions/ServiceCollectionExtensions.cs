using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Behavioral;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Events.Listeners;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Persistence;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Extensions;

/// <summary>
///     Extension methods for configuring bot detection services.
///     All methods are designed to be fail-safe with sensible defaults.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Add bot detection services to the service collection.
    ///     This is the primary registration method supporting multiple detection strategies.
    /// </summary>
    /// <remarks>
    ///     Default configuration enables all heuristic detection (UA, headers, IP, behavioral)
    ///     but disables LLM detection (requires Ollama). All settings can be customized via
    ///     the configure action or appsettings.json.
    /// </remarks>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action (applied after appsettings)</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    ///     // Minimal registration (uses defaults + appsettings.json)
    ///     builder.Services.AddBotDetection();
    ///     // With custom configuration
    ///     builder.Services.AddBotDetection(options =>
    ///     {
    ///     options.BotThreshold = 0.8;
    ///     options.EnableLlmDetection = true;
    ///     });
    /// </example>
    public static IServiceCollection AddBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
    {
        // Configure options from appsettings.json "BotDetection" section
        services.AddOptions<BotDetectionOptions>()
            .BindConfiguration("BotDetection")
            .Configure(options =>
            {
                // Apply any code-based configuration on top of appsettings
                configure?.Invoke(options);
            })
            .ValidateOnStart();

        // Register options validator for fail-fast on invalid config
        services.AddSingleton<IValidateOptions<BotDetectionOptions>, BotDetectionOptionsValidator>();

        // Register core services
        RegisterCoreServices(services);

        return services;
    }

    /// <summary>
    ///     Add bot detection with explicit IConfiguration binding.
    ///     Use this when you need to bind from a non-standard configuration section.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">Configuration section to bind from</param>
    /// <param name="configure">Optional configuration action (applied after binding)</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    ///     // Bind from custom section
    ///     builder.Services.AddBotDetection(
    ///     builder.Configuration.GetSection("MyApp:Security:BotDetection"));
    /// </example>
    public static IServiceCollection AddBotDetection(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<BotDetectionOptions>? configure = null)
    {
        services.AddOptions<BotDetectionOptions>()
            .Bind(configuration)
            .Configure(options => configure?.Invoke(options))
            .ValidateOnStart();

        services.AddSingleton<IValidateOptions<BotDetectionOptions>, BotDetectionOptionsValidator>();

        RegisterCoreServices(services);

        return services;
    }

    /// <summary>
    ///     Add simple bot detection (user-agent only).
    ///     Fastest option with minimal resource usage.
    /// </summary>
    /// <remarks>
    ///     This configuration:
    ///     - Only enables User-Agent pattern matching
    ///     - Disables header analysis, IP detection, behavioral analysis, and LLM
    ///     - Suitable for low-traffic apps or when speed is critical
    /// </remarks>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddSimpleBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
    {
        return services.AddBotDetection(options =>
        {
            options.EnableUserAgentDetection = true;
            options.EnableHeaderAnalysis = false;
            options.EnableIpDetection = false;
            options.EnableBehavioralAnalysis = false;
            options.EnableLlmDetection = false;

            configure?.Invoke(options);
        });
    }

    /// <summary>
    ///     Add comprehensive bot detection (all heuristics, no LLM).
    ///     Recommended for most production applications.
    /// </summary>
    /// <remarks>
    ///     This configuration:
    ///     - Enables User-Agent pattern matching
    ///     - Enables header analysis (Accept, Accept-Language, etc.)
    ///     - Enables IP-based detection (datacenter ranges)
    ///     - Enables behavioral analysis (request rate limiting)
    ///     - Disables LLM detection
    ///     - Good balance of accuracy and performance
    /// </remarks>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddComprehensiveBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
    {
        return services.AddBotDetection(options =>
        {
            options.EnableUserAgentDetection = true;
            options.EnableHeaderAnalysis = true;
            options.EnableIpDetection = true;
            options.EnableBehavioralAnalysis = true;
            options.EnableLlmDetection = false;

            configure?.Invoke(options);
        });
    }

    /// <summary>
    ///     Add advanced bot detection with LLM (requires Ollama).
    ///     Most accurate but requires Ollama to be running.
    /// </summary>
    /// <remarks>
    ///     This configuration:
    ///     - Enables all heuristic detection methods
    ///     - Enables LLM-based semantic analysis
    ///     - Requires Ollama to be running at the specified endpoint
    ///     - Recommended models: qwen3:0.6b, qwen2.5:1.5b, phi3:mini
    ///     LLM detection is fail-safe: if Ollama is unavailable,
    ///     detection continues with heuristics only.
    /// </remarks>
    /// <param name="services">The service collection</param>
    /// <param name="ollamaEndpoint">Ollama endpoint URL (default: http://localhost:11434)</param>
    /// <param name="model">Ollama model name (default: qwen3:0.6b)</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    ///     // With default Ollama settings
    ///     builder.Services.AddAdvancedBotDetection();
    ///     // With custom endpoint and model
    ///     builder.Services.AddAdvancedBotDetection(
    ///     ollamaEndpoint: "http://ollama-server:11434",
    ///     model: "phi3:mini");
    /// </example>
    public static IServiceCollection AddAdvancedBotDetection(
        this IServiceCollection services,
        string ollamaEndpoint = "http://localhost:11434",
        string model = "qwen3:0.6b",
        Action<BotDetectionOptions>? configure = null)
    {
        return services.AddBotDetection(options =>
        {
            options.EnableUserAgentDetection = true;
            options.EnableHeaderAnalysis = true;
            options.EnableIpDetection = true;
            options.EnableBehavioralAnalysis = true;
            options.EnableLlmDetection = true;

            // Use the new AiDetection configuration
            options.AiDetection.Provider = AiProvider.Ollama;
            options.AiDetection.Ollama.Endpoint = ollamaEndpoint;
            options.AiDetection.Ollama.Model = model;

            configure?.Invoke(options);
        });
    }

    /// <summary>
    ///     Configure bot detection options (for post-registration customization).
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration action</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection ConfigureBotDetection(
        this IServiceCollection services,
        Action<BotDetectionOptions> configure)
    {
        services.Configure(configure);
        return services;
    }

    /// <summary>
    ///     Registers core bot detection services.
    ///     Called by all Add*BotDetection methods.
    /// </summary>
    private static void RegisterCoreServices(IServiceCollection services)
    {
        // Add HttpClient factory for bot list fetching
        services.AddHttpClient();

        // Named HttpClient for VerifiedBotContributor (fetches published IP range lists)
        services.AddHttpClient("VerifiedBot", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StyloBot/1.0 (+https://stylobot.net; stylobot@mostlylucid.net)");
        });

        // VerifiedBotRegistry options — configurable via appsettings.json: BotDetection:VerifiedBotRegistry
        services.AddOptions<VerifiedBotRegistryOptions>()
            .BindConfiguration("BotDetection:VerifiedBotRegistry");

        // Add memory cache if not already registered
        services.AddMemoryCache();

        // Register performance infrastructure
        services.TryAddSingleton<BotDetectionMetrics>();
        services.TryAddSingleton<ICompiledPatternCache, CompiledPatternCache>();

        // Register bot list fetcher and database
        services.TryAddSingleton<IBotListFetcher, BotListFetcher>();
        services.TryAddSingleton<IBotListDatabase>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            var fetcher = sp.GetRequiredService<IBotListFetcher>();
            var logger = sp.GetRequiredService<ILogger<BotListDatabase>>();
            return new BotListDatabase(fetcher, logger, options.DatabasePath);
        });

        // Register ASN lookup service (Team Cymru DNS-based IP→ASN mapping)
        services.TryAddSingleton<IAsnLookupService, AsnLookupService>();

        // Register core bot detection service
        services.TryAddSingleton<IBotDetectionService, BotDetectionService>();

        // Register bot list update background service
        services.AddHostedService<BotListUpdateService>();

        // Register detector manifest loader (YAML-based configuration)
        services.TryAddSingleton<DetectorManifestLoader>(sp =>
        {
            var loader = new DetectorManifestLoader();
            // Load embedded manifests on first access
            loader.LoadEmbeddedManifests();
            return loader;
        });

        // Register detector config provider (resolves YAML + appsettings overrides)
        services.TryAddSingleton<IDetectorConfigProvider, DetectorConfigProvider>();

        // Register individual detectors
        // Each detector is responsible for one detection strategy
        // Register as both interface and concrete type for DI flexibility
        services.TryAddSingleton<UserAgentDetector>();
        services.TryAddSingleton<HeaderDetector>();
        services.TryAddSingleton<BehavioralDetector>();
        services.TryAddSingleton<IpDetector>();
        services.TryAddSingleton<LlmDetector>();
        services.TryAddSingleton<HeuristicDetector>();
        services.TryAddSingleton<ClientSideDetector>();
        services.TryAddSingleton<InconsistencyDetector>();
        services.TryAddSingleton<SecurityToolDetector>();

        // Also register as IDetector for generic detector enumeration
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<UserAgentDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<HeaderDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<BehavioralDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<IpDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<LlmDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<HeuristicDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<ClientSideDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<InconsistencyDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<SecurityToolDetector>());

        // Register client-side fingerprinting services
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.TryAddSingleton<IBrowserTokenService, BrowserTokenService>();
        services.TryAddSingleton<IBrowserFingerprintAnalyzer, BrowserFingerprintAnalyzer>();
        services.TryAddSingleton<IBrowserFingerprintStore, BrowserFingerprintStore>();

        // Register signal bus infrastructure (intra-request, event-driven detection)
        services.TryAddTransient<IBotSignalBusFactory, BotSignalBusFactory>();

        // Register signal listeners (react to detection signals)
        services.AddTransient<IBotSignalListener, RiskAssessmentListener>();
        services.AddTransient<IBotSignalListener, LearningListener>();

        // Register signal-driven detection service
        services.TryAddSingleton<SignalDrivenDetectionService>();

        // Register inter-request learning infrastructure
        services.TryAddSingleton<ILearningEventBus, LearningEventBus>();

        // Register learning event handlers
        services.AddSingleton<ILearningEventHandler, InferenceHandler>();
        services.AddSingleton<ILearningEventHandler, PatternAccumulatorHandler>();
        services.AddSingleton<ILearningEventHandler, FeedbackHandler>();
        services.AddSingleton<ILearningEventHandler, DriftDetectionHandler>();

        // Register learning background service (processes learning events asynchronously)
        services.AddHostedService<LearningBackgroundService>();

        // Register fast-path decider for UA short-circuit with sampling
        services.TryAddSingleton<FastPathDecider>();

        // Register learned pattern store (SQLite-backed)
        services.TryAddSingleton<ILearnedPatternStore, SqliteLearnedPatternStore>();

        // Register weight store for learning feedback loop
        services.TryAddSingleton<IWeightStore, SqliteWeightStore>();

        // Register signature feedback handler (feeds learned patterns back to detectors)
        services.AddSingleton<ILearningEventHandler, SignatureFeedbackHandler>();

        // Register common user agent service (scrapes useragents.me for browser versions and common UAs)
        services.TryAddSingleton<ICommonUserAgentService, CommonUserAgentService>();
        services.TryAddSingleton<IBrowserVersionService>(sp =>
            (CommonUserAgentService)sp.GetRequiredService<ICommonUserAgentService>());
        services.AddHostedService(sp => (CommonUserAgentService)sp.GetRequiredService<ICommonUserAgentService>());

        // Register version age detector
        services.TryAddSingleton<VersionAgeDetector>();
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<VersionAgeDetector>());

        // Register pattern reputation system (learning + forgetting)
        services.TryAddSingleton<PatternReputationUpdater>();

        // Use ephemeral-based reputation cache for better observability and hot-key tracking
        // Falls back to InMemoryPatternReputationCache if ephemeral is not available
        services.TryAddSingleton<IPatternReputationCache>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<EphemeralPatternReputationCache>>();
            var updater = sp.GetRequiredService<PatternReputationUpdater>();
            var patternStore = sp.GetRequiredService<ILearnedPatternStore>();
            return new EphemeralPatternReputationCache(logger, updater, patternStore);
        });

        // Register ReputationMaintenanceService as a singleton (single instance for both interfaces)
        services.AddSingleton<ReputationMaintenanceService>();
        services.AddSingleton<ILearningEventHandler>(sp => sp.GetRequiredService<ReputationMaintenanceService>());
        services.AddHostedService(sp => sp.GetRequiredService<ReputationMaintenanceService>());

        // ==========================================
        // Blackboard Orchestrator (event-driven, parallel detection)
        // ==========================================

        // Register cross-request signature coordinator (singleton - tracks across all requests)
        services.TryAddSingleton<SignatureCoordinator>();

        // Register response coordinator (tracks response patterns for behavioral feedback)
        services.TryAddSingleton<ResponseCoordinator>();

        // Register PiiHasher for zero-PII signature generation
        // Key should ideally come from secure config (Key Vault, env var), but auto-generate if not provided
        services.TryAddSingleton(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            // Check if a key is configured via SignatureHashKey (base64)
            if (!string.IsNullOrEmpty(options.SignatureHashKey))
            {
                return PiiHasher.FromBase64Key(options.SignatureHashKey);
            }
            // Auto-generate key for development/testing (logs warning)
            var logger = sp.GetService<ILogger<PiiHasher>>();
            logger?.LogWarning("PiiHasher using auto-generated key. Configure BotDetection:SignatureHashKey for production.");
            return new PiiHasher(PiiHasher.GenerateKey());
        });

        // Register both orchestrators - ephemeral for new architecture, blackboard for compatibility
        services.TryAddSingleton<BlackboardOrchestrator>();
        services.TryAddSingleton<EphemeralDetectionOrchestrator>();

        // Register contributing detectors (new architecture)
        // These emit evidence, not verdicts - the orchestrator aggregates
        //
        // PRE-Wave 0 - Fast path reputation check (can short-circuit ALL processing)
        // Checks for ConfirmedBad/ManuallyBlocked patterns before any analysis
        services.AddSingleton<IContributingDetector, FastPathReputationContributor>();
        // Verified bot identity check (priority 4) - IP range + FCrDNS verification
        // Runs after FastPathReputation, before UserAgent. Catches spoofed bot UAs.
        services.TryAddSingleton<VerifiedBotRegistry>();
        services.AddHostedService(sp => sp.GetRequiredService<VerifiedBotRegistry>());
        services.AddSingleton<IContributingDetector, VerifiedBotContributor>();
        // TimescaleDB historical reputation - runs early (priority 15)
        // Gracefully no-ops if ITimescaleReputationProvider is not registered
        services.AddSingleton<IContributingDetector, TimescaleReputationContributor>();
        //
        // Wave 0 detectors (no dependencies - run first)
        services.AddSingleton<IContributingDetector, UserAgentContributor>();
        services.AddSingleton<IContributingDetector, HeaderContributor>();
        services.AddSingleton<IContributingDetector, IpContributor>();
        services.AddSingleton<IContributingDetector, BehavioralContributor>();
        services.AddSingleton<IContributingDetector, ClientSideContributor>();
        // Attack payload detection - runs before SecurityTool, catches injection/scanning patterns
        services.AddSingleton<IContributingDetector, HaxxorContributor>();
        // Security tool detection - runs early with UA analysis
        services.AddSingleton<IContributingDetector, SecurityToolContributor>();
        // AI scraper detection - known AI bots, Cloudflare signals, Web Bot Auth
        services.AddSingleton<IContributingDetector, AiScraperContributor>();
        // Cache behavior analysis - runs early alongside behavioral
        services.AddSingleton<IContributingDetector, CacheBehaviorContributor>();
        // Advanced behavioral pattern detection - runs after basic behavioral
        services.AddSingleton<IContributingDetector, AdvancedBehavioralContributor>();
        // Advanced fingerprinting detectors (Wave 0 - network/protocol layer)
        services.AddSingleton<IContributingDetector, TlsFingerprintContributor>();
        services.AddSingleton<IContributingDetector, TcpIpFingerprintContributor>();
        services.AddSingleton<IContributingDetector, Http2FingerprintContributor>();
        services.AddSingleton<IContributingDetector, Http3FingerprintContributor>();
        // Response behavior feedback - runs early to provide historical feedback
        services.AddSingleton<IContributingDetector, ResponseBehaviorContributor>();
        // Wave 1+ detectors (triggered by signals from Wave 0)
        // Account takeover detection - credential stuffing, brute force, ATO drift (triggered by ua.family/waveform.signature)
        services.AddSingleton<IContributingDetector, AccountTakeoverContributor>();
        // Geo change detection - country drift and country reputation (triggered by geo.country_code)
        services.AddSingleton<IContributingDetector, GeoChangeContributor>();
        services.AddSingleton<IContributingDetector, VersionAgeContributor>();
        services.AddSingleton<IContributingDetector, InconsistencyContributor>();
        // Project Honeypot IP reputation (triggered by IP signal)
        services.AddSingleton<IContributingDetector, ProjectHoneypotContributor>();
        // Reputation bias - runs AFTER basic detectors extract signals, BEFORE heuristic scoring
        // Provides learned pattern bias from PatternReputationCache
        services.AddSingleton<IContributingDetector, ReputationBiasContributor>();
        // Heuristic early - runs before AI with basic request features
        services.AddSingleton<IContributingDetector, HeuristicContributor>();
        // Multi-layer correlation - runs after fingerprinting to cross-check consistency
        services.AddSingleton<IContributingDetector, MultiLayerCorrelationContributor>();
        // Behavioral waveform analysis - analyzes patterns across multiple requests
        services.AddSingleton<IContributingDetector, BehavioralWaveformContributor>();
        // Bot cluster detection - discovers bot products and coordinated campaigns
        services.TryAddSingleton<CountryReputationTracker>();
        services.TryAddSingleton<BotClusterService>();
        services.AddHostedService(sp => sp.GetRequiredService<BotClusterService>());
        // Signature convergence - merges/splits related signatures (same IP, rotating UAs)
        services.TryAddSingleton<SignatureConvergenceService>();
        services.AddHostedService(sp => sp.GetRequiredService<SignatureConvergenceService>());
        services.AddSingleton<IContributingDetector, ClusterContributor>();
        // LLM-based cluster descriptions (background, never in request pipeline)
        services.AddSingleton<BotClusterDescriptionService>();

        // ==========================================
        // Bot Name Synthesizer (LLamaSharp or Ollama)
        // ==========================================
        // Synthesizes human-readable bot names from detection signals
        services.AddSingleton<IBotNameSynthesizer>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<LlamaSharpBotNameSynthesizer>>();

            return opts.AiDetection.Provider switch
            {
                AiProvider.LlamaSharp => new LlamaSharpBotNameSynthesizer(logger, sp.GetRequiredService<IOptions<BotDetectionOptions>>()),
                _ => new LlamaSharpBotNameSynthesizer(logger, sp.GetRequiredService<IOptions<BotDetectionOptions>>())
            };
        });

        // ==========================================
        // Signature Description Service (Background)
        // ==========================================
        // Generates LLM descriptions for signatures once they reach request threshold.
        // Registered as singleton + hosted service so the broadcast middleware can inject it.
        services.AddSingleton<SignatureDescriptionService>();
        services.AddHostedService(sp => sp.GetRequiredService<SignatureDescriptionService>());

        // Similarity search - runs after Heuristic (priority 60) to leverage feature extraction
        services.AddSingleton<IContributingDetector, SimilarityContributor>();
        // AI/LLM detectors (run when escalation triggered or in demo mode)
        services.AddSingleton<IContributingDetector, LlmContributor>();
        // Heuristic late - runs AFTER AI (or after all static if no AI), consumes all evidence
        services.AddSingleton<IContributingDetector, HeuristicLateContributor>();

        // ==========================================
        // Background LLM Classification Coordinator
        // ==========================================
        services.AddSingleton<LlmClassificationCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<LlmClassificationCoordinator>());

        // ==========================================
        // Similarity Search (HNSW or Qdrant)
        // ==========================================

        // Feature vectorizer converts dynamic feature dictionaries to fixed-length vectors
        services.TryAddSingleton<FeatureVectorizer>();

        // ONNX embedding provider (optional - for ML-powered similarity)
        services.TryAddSingleton<IEmbeddingProvider>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            var logger = sp.GetRequiredService<ILogger<OnnxEmbeddingProvider>>();
            return new OnnxEmbeddingProvider(opts.Qdrant, opts.DatabasePath, logger);
        });

        // Always register HNSW as concrete type for fallback
        services.TryAddSingleton<HnswFileSimilaritySearch>();

        // Qdrant or HNSW based on configuration
        services.TryAddSingleton<ISignatureSimilaritySearch>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            if (opts.Qdrant.Enabled)
            {
                var qdrantLogger = sp.GetRequiredService<ILogger<QdrantSimilaritySearch>>();
                if (opts.Qdrant.EnableEmbeddings)
                {
                    var embedder = sp.GetRequiredService<IEmbeddingProvider>();
                    var vectorizer = sp.GetRequiredService<FeatureVectorizer>();
                    return new DualVectorSimilaritySearch(opts.Qdrant, vectorizer, embedder, opts.DatabasePath, qdrantLogger);
                }
                return new QdrantSimilaritySearch(opts.Qdrant, opts.DatabasePath, qdrantLogger);
            }
            // Fallback: file-backed HNSW (current behavior)
            return sp.GetRequiredService<HnswFileSimilaritySearch>();
        });

        // Learning handler that feeds high-confidence detections into the similarity index
        services.AddSingleton<ILearningEventHandler, SimilarityLearningHandler>();

        // ==========================================
        // Behavioral Signature / BDF System (closed-loop testing)
        // ==========================================

        // Configure BDF mapper options (thresholds for mapping behavior → scenarios)
        services.AddOptions<SignatureToBdfMapperOptions>()
            .BindConfiguration("BotDetection:BdfMapper")
            .ValidateOnStart();

        // Register BDF mapper (maps observed behavior to synthetic test scenarios)
        services.TryAddSingleton<SignatureToBdfMapper>();

        // Register explanation formatter (human-readable dashboard explanations)
        services.TryAddSingleton<ISignatureExplanationFormatter, SignatureExplanationFormatter>();

        // Register BDF runner (executes BDF scenarios for closed-loop testing)
        services.TryAddSingleton<IBdfRunner, BdfRunner>();

        // ==========================================
        // Background Services
        // ==========================================

        // Anomaly saver - writes detection events to rolling JSON files (opt-in)
        services.AddHostedService<AnomalySaverService>();

        // ==========================================
        // Policy System (path-based detection workflows)
        // ==========================================

        // Register policy registry (holds named policies)
        services.TryAddSingleton<IPolicyRegistry, PolicyRegistry>();

        // Register policy evaluator (handles transitions and weight resolution)
        services.TryAddSingleton<IPolicyEvaluator, PolicyEvaluator>();

        // ==========================================
        // Action Policy System (composable response handling)
        // ==========================================

        // Register action policy factories (create policies from configuration)
        services.AddSingleton<IActionPolicyFactory, BlockActionPolicyFactory>();
        services.AddSingleton<IActionPolicyFactory, ThrottleActionPolicyFactory>();
        services.AddSingleton<IActionPolicyFactory, ChallengeActionPolicyFactory>();
        services.AddSingleton<IActionPolicyFactory, RedirectActionPolicyFactory>();
        services.AddSingleton<IActionPolicyFactory, LogOnlyActionPolicyFactory>();

        // Register action policy registry (holds named action policies)
        services.TryAddSingleton<IActionPolicyRegistry, ActionPolicyRegistry>();
    }
}