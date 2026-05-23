using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Behavioral;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Detectors;
// LlmDetector removed - now in Mostlylucid.BotDetection.Llm.Ollama/LlamaSharp packages
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Events.Listeners;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Telemetry;
using Mostlylucid.BotDetection.ThreatIntel;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Audit;
using Mostlylucid.BotDetection.Orchestration.ContributingDetectors;
using Mostlylucid.BotDetection.Orchestration.Manifests;
using Mostlylucid.BotDetection.Persistence;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.BotDetection.Compliance;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.BotDetection.Setup;
using Mostlylucid.BotDetection.SimulationPacks;

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
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
            options.EnableUserAgentDetection = true;
            options.EnableHeaderAnalysis = false;
            options.EnableIpDetection = false;
            options.EnableBehavioralAnalysis = false;
            options.EnableLlmDetection = false;
#pragma warning restore CS0618

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
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
            options.EnableUserAgentDetection = true;
            options.EnableHeaderAnalysis = true;
            options.EnableIpDetection = true;
            options.EnableBehavioralAnalysis = true;
            options.EnableLlmDetection = false;
#pragma warning restore CS0618

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
    ///     - Recommended models: gemma4, qwen3:0.6b, phi3:mini
    ///     LLM detection is fail-safe: if Ollama is unavailable,
    ///     detection continues with heuristics only.
    /// </remarks>
    /// <param name="services">The service collection</param>
    /// <param name="ollamaEndpoint">Ollama endpoint URL</param>
    /// <param name="model">Ollama model name</param>
    /// <param name="configure">Optional configuration action</param>
    /// <returns>The service collection for chaining</returns>
    /// <example>
    ///     // With default Ollama settings (gemma4)
    ///     builder.Services.AddAdvancedBotDetection();
    ///     // With custom endpoint and model
    ///     builder.Services.AddAdvancedBotDetection(
    ///     ollamaEndpoint: "http://ollama-server:11434",
    ///     model: "phi3:mini");
    /// </example>
    public static IServiceCollection AddAdvancedBotDetection(
        this IServiceCollection services,
        string ollamaEndpoint = LlmDefaults.DefaultEndpoint,
        string model = LlmDefaults.DefaultModel,
        Action<BotDetectionOptions>? configure = null)
    {
        return services.AddBotDetection(options =>
        {
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
            options.EnableUserAgentDetection = true;
            options.EnableHeaderAnalysis = true;
            options.EnableIpDetection = true;
            options.EnableBehavioralAnalysis = true;
            options.EnableLlmDetection = true;
#pragma warning restore CS0618

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
    ///     Registers setup services for <c>stylobot setup</c> command.
    ///     Called automatically by AddBotDetection(). Can also be called in isolation
    ///     by the Console setup command for a minimal host without the full detector stack.
    /// </summary>
    public static IServiceCollection AddBotDetectionSetupServices(this IServiceCollection services)
    {
        services.TryAddSingleton<IBotListFetcher, BotListFetcher>();
        services.TryAddSingleton<IBotListDatabase>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            var fetcher = sp.GetRequiredService<IBotListFetcher>();
            var logger = sp.GetRequiredService<ILogger<BotListDatabase>>();
            return new BotListDatabase(fetcher, logger, options.DatabasePath);
        });
        services.TryAddEnumerable(ServiceDescriptor.Singleton<ISetupResource, BotListSetupResource>());
        services.TryAddSingleton<SetupService>();
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

        // VerifiedBotRegistry options - configurable via appsettings.json: BotDetection:VerifiedBotRegistry
        services.AddOptions<VerifiedBotRegistryOptions>()
            .BindConfiguration("BotDetection:VerifiedBotRegistry");

        // Proxy topology sensing: auto-detects the CDN/proxy in front of the app and
        // resolves the real client IP from the correct headers (CF-Connecting-IP, X-Real-IP, etc.).
        // Registered as singleton - topology is detected once on first request and cached.
        services.TryAddSingleton<IProxyEnvironment, ProxyEnvironmentDetector>();

        // Add memory cache if not already registered
        services.AddMemoryCache();

        // Register bot pattern loader (YAML-driven, replaces hardcoded bot lists)
        services.TryAddSingleton<Definitions.BotPatterns.BotPatternLoader>();

        // Register performance infrastructure
        services.TryAddSingleton<BotDetectionMetrics>();
        services.TryAddSingleton<ICompiledPatternCache, CompiledPatternCache>();

        // Telemetry (required by middleware - always register defaults;
        // AddBotDetectionTelemetry() can override with custom config)
        services.AddOptions<Telemetry.BotDetectionTelemetryOptions>()
            .BindConfiguration("BotDetection:Telemetry");
        services.TryAddSingleton<Telemetry.BotDetectionSignalMeter>();
        services.TryAddSingleton<Telemetry.BotDetectionInstrumentation>();

        // Register setup services (bot list, ONNX model, setup resources, SetupService)
        services.AddBotDetectionSetupServices();

        // Register ASN lookup service (Team Cymru DNS-based IP→ASN mapping)
        services.TryAddSingleton<IAsnLookupService, AsnLookupService>();

        // Register core bot detection service
        services.TryAddSingleton<IBotDetectionService, BotDetectionService>();

        // Register API key store (config-backed, can be overridden for DB-backed)
        services.TryAddSingleton<IApiKeyStore, InMemoryApiKeyStore>();

        // Domain entitlement validator (licensed-domain enforcement, warn-never-lock).
        // No-op when BotDetection:Licensing:Domains is empty (OSS / unconfigured default).
        services.AddDomainEntitlement();

        // License state: FossLicenseState when no token, real enforcement when token present
        services.AddSingleton<SqliteLicenseGraceStore>();
        services.AddSingleton<LicenseState>();
        services.AddSingleton<ILicenseState>(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            if (!string.IsNullOrWhiteSpace(opts.Licensing?.Token))
                return sp.GetRequiredService<LicenseState>();
            return new FossLicenseState();
        });
        services.AddHostedService<LicenseStateRefreshService>(sp =>
            new LicenseStateRefreshService(
                sp.GetRequiredService<LicenseState>(),
                sp.GetRequiredService<IOptionsMonitor<BotDetectionOptions>>(),
                sp.GetRequiredService<SqliteLicenseGraceStore>(),
                sp.GetRequiredService<ILogger<LicenseStateRefreshService>>()));

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

        // Register detector config provider (resolves YAML + appsettings overrides).
        // Commercial packages may register IConfigurationOverrideSource implementations
        // for live per-target config overrides (Postgres + Redis pub/sub).
        services.TryAddSingleton<IDetectorConfigProvider, DetectorConfigProvider>();
        // Background watcher that invalidates cache when override sources emit changes.
        // No-op when no override sources are registered (FOSS default).
        services.AddHostedService<ConfigurationWatcher>();

        // Fleet telemetry dispatcher - fans out detection reports to IFleetReporter
        // implementations (commercial plugin registers the control plane reporter).
        // No-op when no reporters are registered (FOSS default).
        services.TryAddSingleton<Orchestration.Telemetry.FleetReportDispatcher>();

        // Real-time detection event publisher - default no-op. Commercial Redis cluster
        // package replaces this with a pub/sub fan-out so a separate Stylobot-UI
        // container can render live events without being in the request path.
        services.TryAddSingleton<Orchestration.Telemetry.IDetectionEventPublisher,
            Orchestration.Telemetry.NullDetectionEventPublisher>();

        // Audit processors read the same raw signal trace used by the detection pipeline
        // and emit derived audit records to one or more sinks. Commercial packages can
        // add processor/sink packs without replacing the FOSS defaults.
        services.AddOptions<AuditProcessorOptions>()
            .BindConfiguration("BotDetection:AuditProcessors");
        services.TryAddSingleton<AuditProcessorDispatcher>();
        services.TryAddSingleton<IAuditRecordWriter, AuditRecordWriter>();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditSink, LoggerAuditSink>());
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IAuditProcessor, ErrorSignalAuditProcessor>());

        // FOSS hot-reload: watch {ContentRoot}/stylobot-config for YAML/JSON edits and
        // invalidate the DetectorConfigProvider cache on change. Hosted service starts
        // the watcher; registered as IConfigurationOverrideSource so ConfigurationWatcher
        // subscribes to its change stream. Creates the directory (with a README) on first
        // start - no effect if the operator deletes it later.
        // Lambda registration so IHostEnvironment is *optional* - unit-test fixtures that
        // build a plain ServiceCollection without a host can still resolve the singleton.
        // The override source falls back to AppContext.BaseDirectory when env is null.
        services.TryAddSingleton(sp => new Orchestration.Manifests.FileSystemConfigurationOverrideSource(
            sp.GetRequiredService<Orchestration.Manifests.DetectorManifestLoader>(),
            sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>(),
            sp.GetRequiredService<ILogger<Orchestration.Manifests.FileSystemConfigurationOverrideSource>>()));
        services.AddSingleton<Orchestration.Manifests.IConfigurationOverrideSource>(sp =>
            sp.GetRequiredService<Orchestration.Manifests.FileSystemConfigurationOverrideSource>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<Orchestration.Manifests.FileSystemConfigurationOverrideSource>());

        // Editor service used by the dashboard's Configuration tab to list/read/write
        // detector manifest overrides. Reads embedded manifests from this assembly + writes
        // to the same directory the override source watches. Both interface and concrete are
        // registered: the dashboard middleware resolves IConfigEditorService for reads
        // (substitutable with a remote impl), the concrete for writes.
        services.TryAddSingleton<Orchestration.Manifests.ConfigEditorService>();
        services.TryAddSingleton<Orchestration.Manifests.IConfigEditorService>(
            sp => sp.GetRequiredService<Orchestration.Manifests.ConfigEditorService>());

        // Register individual detectors
        // Each detector is responsible for one detection strategy
        // Register as both interface and concrete type for DI flexibility
        services.TryAddSingleton<UserAgentDetector>();
        services.TryAddSingleton<HeaderDetector>();
        services.TryAddSingleton<BehavioralDetector>();
        services.TryAddSingleton<IpDetector>();
        services.TryAddSingleton<HeuristicDetector>();
        services.TryAddSingleton<ClientSideDetector>();
        services.TryAddSingleton<InconsistencyDetector>();
        services.TryAddSingleton<SecurityToolDetector>();

        // Also register as IDetector for generic detector enumeration
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<UserAgentDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<HeaderDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<BehavioralDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<IpDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<HeuristicDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<ClientSideDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<InconsistencyDetector>());
        services.AddSingleton<IDetector>(sp => sp.GetRequiredService<SecurityToolDetector>());

        // Register client-side fingerprinting services
        services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        services.TryAddSingleton<IBrowserTokenService, BrowserTokenService>();
        services.TryAddSingleton<IBrowserFingerprintAnalyzer, BrowserFingerprintAnalyzer>();
        services.TryAddSingleton<IBrowserFingerprintStore, BrowserFingerprintStore>();
        services.TryAddSingleton<FingerprintPopulationTracker>();
        services.TryAddSingleton<DeploymentNormTracker>(sp =>
        {
            var opts = sp.GetService<IOptions<BotDetectionOptions>>()?.Value;
            return new DeploymentNormTracker(
                windowSize: opts?.PopulationWindowSize ?? 500,
                warmupRequests: opts?.PopulationWarmupRequests ?? 50);
        });

        // Register signal bus infrastructure (intra-request, event-driven detection)
        services.TryAddTransient<IBotSignalBusFactory, BotSignalBusFactory>();

        // Register signal listeners (react to detection signals)
        services.AddTransient<IBotSignalListener, RiskAssessmentListener>();
        services.AddTransient<IBotSignalListener, LearningListener>();

        // Register signal-driven detection service
        services.TryAddSingleton<SignalDrivenDetectionService>();

        // Register inter-request learning infrastructure.
        // LearningEventBus is the real implementation (10k-entry channel, DropOldest).
        // BoundedChannelLearningBus wraps it with a smaller front-end channel (~20ns TryWrite)
        // when HighPerformanceMode is enabled; otherwise it passes through with zero overhead.
        services.TryAddSingleton<LearningEventBus>();
        services.TryAddSingleton<BoundedChannelLearningBus>();
        services.TryAddSingleton<ILearningEventBus>(sp => sp.GetRequiredService<BoundedChannelLearningBus>());
        services.AddHostedService(sp => sp.GetRequiredService<BoundedChannelLearningBus>());

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

        // Register variance watchdog (singleton - per-signature observation history used by the verdict gate)
        services.TryAddSingleton<Services.VarianceWatchdog>();

        // Register the signature verdict gate (singleton - thin decision wrapper over the coordinator).
        // Explicit factory so the optional IdentityVerdictLookup parameter actually gets resolved -
        // the bare TryAddSingleton<T>() form relies on conventional constructor injection which
        // does NOT honour C# default parameter values; it would supply null even when the lookup
        // is registered. Without this factory the "metastable cached verdict alongside the
        // per-signature aggregate" path documented around the IdentityVerdictLookup registration
        // is silently dead.
        services.TryAddSingleton<Services.SignatureVerdictGate>(sp =>
            new Services.SignatureVerdictGate(
                sp.GetRequiredService<SignatureCoordinator>(),
                sp.GetRequiredService<ILogger<Services.SignatureVerdictGate>>(),
                sp.GetService<Identity.IdentityVerdictLookup>()));

        // Register response coordinator (tracks response patterns for behavioral feedback)
        services.TryAddSingleton<ResponseCoordinator>();
        services.TryAddSingleton<IResponsePiiMasker, MicrosoftRecognizersResponsePiiMasker>();

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
            logger?.LogInformation("PiiHasher using a session-scoped random key. Signatures will not persist across restarts. Set BotDetection:SignatureHashKey in production for stable cross-session identity.");
            return new PiiHasher(PiiHasher.GenerateKey());
        });

        // Register multi-factor signature service for visitor identity correlation
        services.TryAddSingleton<MultiFactorSignatureService>();

        // EphemeralDetectionOrchestrator is the active orchestrator; BlackboardOrchestrator kept for direct injection in tests
        services.TryAddSingleton<BlackboardOrchestrator>();
        services.TryAddSingleton<EphemeralDetectionOrchestrator>();
        services.TryAddSingleton<IDetectionOrchestrator>(sp => sp.GetRequiredService<EphemeralDetectionOrchestrator>());

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
        // Fediverse domain verification (priority 5) - NodeInfo lookup against the
        // +https://instance/ URL in Mastodon/Pleroma/Misskey UAs. The cross-
        // corroboration analogue to IP-range verification for traffic that runs
        // on arbitrary cloud IPs. Verifier uses a typed HttpClient with strict
        // SSRF guards (no IP literals, no .local/.localhost/.invalid/.internal,
        // https-only, 3s timeout, 32KB max body). Cache: 24h positive / 1h
        // negative -- hot path is a dictionary lookup, only first-encounter
        // domains pay outbound HTTPS cost.
        services.AddHttpClient<IFediverseDomainVerifier, FediverseDomainVerifier>(client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("stylobot-nodeinfo-verifier/1.0 (+https://stylobot.net)");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,  // SSRF guard -- no redirect chains
            UseCookies = false
        });
        services.AddSingleton<IContributingDetector, FediverseDomainContributor>();
        // Threat-intel enrichment (priority 7) - reads cached verdicts from
        // IThreatIntelCoordinator (offline pack: Spamhaus, Tor, KEV, cloud ranges).
        // FOSS default: coordinator IsEnabled=false (master switch off + every
        // provider disabled) - contributor registered but short-circuits with no work.
        // Operator opts in by flipping BotDetection:ThreatIntel:Enabled = true AND
        // enabling the providers they want. See docs/architecture/threat-intel.md.
        services.TryAddSingleton<ThreatIntel.IThreatIntelCoordinator, ThreatIntel.ThreatIntelCoordinator>();
        services.TryAddSingleton<ThreatIntel.ThreatIntelEnrichmentQueue>();
        services.AddHostedService<ThreatIntel.ThreatIntelRefreshService>();
        services.AddHostedService<ThreatIntel.ThreatIntelEnrichmentService>();
        services.AddHttpClient<ThreatIntel.Providers.SpamhausDropProvider>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("StyloBot/threatintel");
        });
        services.AddHttpClient<ThreatIntel.Providers.TorExitProvider>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(30);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("StyloBot/threatintel");
        });
        services.AddHttpClient<ThreatIntel.Providers.CisaKevProvider>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);  // KEV is larger; allow more time
            c.DefaultRequestHeaders.UserAgent.ParseAdd("StyloBot/threatintel");
        });
        services.AddHttpClient<ThreatIntel.Providers.CloudRangesProvider>(c =>
        {
            c.Timeout = TimeSpan.FromSeconds(60);  // Aggregates many vendor feeds
            c.DefaultRequestHeaders.UserAgent.ParseAdd("StyloBot/threatintel");
        });
        services.AddSingleton<ThreatIntel.IThreatIntelProvider>(sp =>
            sp.GetRequiredService<ThreatIntel.Providers.SpamhausDropProvider>());
        services.AddSingleton<ThreatIntel.IThreatIntelProvider>(sp =>
            sp.GetRequiredService<ThreatIntel.Providers.TorExitProvider>());
        services.AddSingleton<ThreatIntel.IThreatIntelProvider>(sp =>
            sp.GetRequiredService<ThreatIntel.Providers.CisaKevProvider>());
        services.AddSingleton<ThreatIntel.IThreatIntelProvider>(sp =>
            sp.GetRequiredService<ThreatIntel.Providers.CloudRangesProvider>());
        services.AddSingleton<IContributingDetector, ThreatIntelContributor>();
        // Wave 0 detectors (no dependencies - run first)
        // Unified signature - computes PrimarySignature + header hashes for all downstream detectors (priority 1)
        services.AddSingleton<IContributingDetector, SignatureContributor>();
        // PII query string detection - privacy signals, not bot detection (priority 8)
        services.AddSingleton<IContributingDetector, PiiQueryStringContributor>();
        services.AddSingleton<IContributingDetector, UserAgentContributor>();
        // Identity (metastable fingerprint match). See docs/architecture/fingerprint-match.md.
        // Both contributors are foundation, dormant when Identity.Enabled = false.
        services.TryAddSingleton(sp => Identity.IdentityVectorLayout.DefaultV1());
        services.TryAddSingleton<Identity.IdentityVectorEncoder>();
        services.TryAddSingleton<Identity.SqliteFingerprintStore>();
        // Surface the read-only fingerprint interface so the dashboard / REST endpoints
        // resolve it without depending on the concrete store - swapped for a HTTP-backed
        // impl in remote-mode dashboard hosts.
        services.TryAddSingleton<Identity.IFingerprintReader>(
            sp => sp.GetRequiredService<Identity.SqliteFingerprintStore>());
        // Anchor index: vec0 wrapper that dispatches to brute force when sqlite-vec didn't
        // load. Both impls registered as concrete types so the wrapper can fall back per-call.
        services.TryAddSingleton<Identity.BruteForceIdentityAnchorIndex>();
        services.TryAddSingleton<Identity.IIdentityAnchorIndex, Identity.SqliteVecIdentityAnchorIndex>();
        services.TryAddSingleton<Identity.IdentityArchetypeRegistry>();
        services.AddSingleton<Identity.IdentityGlobalWeightsCache>();
        services.AddHostedService(sp => sp.GetRequiredService<Identity.IdentityGlobalWeightsCache>());
        // Slow-path coordinator: bounded queue, priority scheduling, per-fp coalesce,
        // circuit breaker. Fast path bypasses it; slow path goes through it so adversarial
        // bursts cannot starve legitimate slow-path enrichment.
        services.AddSingleton<Identity.IdentityProcessingCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<Identity.IdentityProcessingCoordinator>());
        // Verdict-gate composition: lets SignatureVerdictGate read the metastable cached
        // verdict alongside the per-signature aggregate. Internally returns null when
        // Identity:Enabled is false, so wiring is unconditional and zero-cost when off.
        services.TryAddSingleton<Identity.IdentityVerdictLookup>();
        // Operator-triggered AI opinion path. Returns a structured "no-llm-provider" result
        // when no Llm package is registered, so the dashboard surface stays well-defined.
        services.TryAddSingleton<Identity.IdentityAiOpinionService>();
        services.AddSingleton<IContributingDetector, IdentityVectorContributor>();
        services.AddSingleton<IContributingDetector, FingerprintMatchContributor>();
        services.AddSingleton<Identity.FingerprintAbsorptionService>();
        services.AddHostedService(sp => sp.GetRequiredService<Identity.FingerprintAbsorptionService>());
        services.AddSingleton<Identity.FingerprintDriftService>();
        services.AddHostedService(sp => sp.GetRequiredService<Identity.FingerprintDriftService>());
        services.AddSingleton<Identity.IdentityWeightCalibrationService>();
        services.AddHostedService(sp => sp.GetRequiredService<Identity.IdentityWeightCalibrationService>());
        services.AddSingleton<IContributingDetector, HeaderContributor>();
        services.AddSingleton<IContributingDetector, IpContributor>();
        services.AddSingleton<IContributingDetector, BehavioralContributor>();
        services.AddSingleton<IContributingDetector, ClientSideContributor>();
        // Attack payload detection - runs before SecurityTool, catches injection/scanning patterns
        services.AddSingleton<IContributingDetector, HaxxorContributor>();
        // Security tool detection - runs early with UA analysis
        services.AddSingleton<IContributingDetector, SecurityToolContributor>();
        // Simulation pack CVE probe detection - matches request paths against loaded packs
        services.TryAddSingleton<ISimulationPackRegistry, SimulationPackLoader>();
        services.AddSingleton<IContributingDetector, CveProbeContributor>();
        // Honeypot path detection (Tier 1 + Tier 2 catalog). Reads the pre-detection
        // tagger's HttpContext.Items classification on the fast path and falls back to
        // running the classifier itself when the tagger middleware isn't wired.
        services.AddOptions<Honeypot.HoneypotDetectionOptions>()
            .BindConfiguration(Honeypot.HoneypotDetectionOptions.SectionName);
        services.TryAddSingleton<Honeypot.IHoneypotExemptStore, Honeypot.ConfigHoneypotExemptStore>();
        services.AddSingleton<IContributingDetector, Honeypot.HoneypotLinkContributor>();
        // Honeypot response policy -- jittered rate-limit + fake response.
        // Registered as IActionPolicy under name "honeypot-response"; the middleware
        // auto-selects it when the tagger set a tier tag on HttpContext.Items.
        services.AddSingleton<Honeypot.HoneypotResponseActionPolicy>();
        services.AddSingleton<IActionPolicy>(sp => sp.GetRequiredService<Honeypot.HoneypotResponseActionPolicy>());
        // AI scraper detection - known AI bots, Cloudflare signals, Web Bot Auth
        services.AddSingleton<IContributingDetector, AiScraperContributor>();
        // Cache behavior analysis - runs early alongside behavioral
        services.AddSingleton<IContributingDetector, CacheBehaviorContributor>();
        // Cookie behavior analysis - detects bots that ignore Set-Cookie headers
        services.AddSingleton<IContributingDetector, CookieBehaviorContributor>();
        // Header correlation - detects UA rotation via identical non-UA header profiles (priority 21)
        services.AddSingleton<IContributingDetector, HeaderCorrelationContributor>();
        // Resource waterfall detection - document-to-asset ratio analysis (priority 22)
        services.AddSingleton<IContributingDetector, ResourceWaterfallContributor>();
        // Behavioral (basic + advanced statistical) pattern detection - merged single contributor
        // Advanced fingerprinting detectors (Wave 0 - network/protocol layer)
        services.AddSingleton<IContributingDetector, TlsFingerprintContributor>();
        services.AddSingleton<IContributingDetector, TcpIpFingerprintContributor>();
        services.AddSingleton<IContributingDetector, Http2FingerprintContributor>();
        services.AddSingleton<IContributingDetector, Http3FingerprintContributor>();
        // Transport protocol detection (WebSocket, gRPC, GraphQL, SSE)
        services.AddSingleton<IContributingDetector, TransportProtocolContributor>();
        // Stream abuse detection (handshake storms, cross-endpoint mixing, reconnect abuse)
        services.AddSingleton<IContributingDetector, StreamAbuseContributor>();
        // Response behavior feedback - runs early to provide historical feedback
        services.AddSingleton<IContributingDetector, ResponseBehaviorContributor>();
        // Click fraud detection - monitors ad click patterns for fraud signals (priority 38)
        services.AddSingleton<IContributingDetector, ClickFraudContributor>();
        // Intent / threat scoring - produces unified threat score orthogonal to bot probability
        services.AddSingleton<IContributingDetector, IntentContributor>();
        // Wave 1+ detectors (triggered by signals from Wave 0)
        // Account takeover detection - credential stuffing, brute force, ATO drift (triggered by ua.family/waveform.signature)
        services.AddSingleton<IContributingDetector, AccountTakeoverContributor>();
        // Geo change detection - country drift and country reputation (triggered by geo.country_code)
        services.AddSingleton<IContributingDetector, GeoChangeContributor>();
        services.AddSingleton<IContributingDetector, VersionAgeContributor>();
        services.AddSingleton<IContributingDetector, InconsistencyContributor>();
        // Project Honeypot lookup service (shared between contributor and background enrichment)
        services.TryAddSingleton<ProjectHoneypotLookupService>();
        // Project Honeypot IP reputation (triggered by IP signal)
        // Excluded from default policy - runs via BackgroundEnrichmentService for async DNS lookups.
        // Still runs synchronously in Learning/Demo policies.
        services.AddSingleton<IContributingDetector, ProjectHoneypotContributor>();
        // Reputation bias - runs AFTER basic detectors extract signals, BEFORE heuristic scoring
        // Provides learned pattern bias from PatternReputationCache
        services.AddSingleton<IContributingDetector, ReputationBiasContributor>();
        // Heuristic early - runs before AI with basic request features
        services.AddSingleton<IContributingDetector, HeuristicContributor>();
        // Multi-layer correlation - runs after fingerprinting to cross-check consistency
        services.AddSingleton<IContributingDetector, MultiLayerCorrelationContributor>();
        // Behavioral waveform analysis - analyzes patterns across multiple requests
        services.AddSingleton<BehavioralWaveformContributor>();
        services.AddSingleton<IContributingDetector>(sp => sp.GetRequiredService<BehavioralWaveformContributor>());
        // Header hash collector for progressive identity resolution
        services.TryAddSingleton<Identity.HeaderHashCollector>();
        // Session vector analysis - Markov chain compression for inter-session anomaly detection
        services.TryAddSingleton<Analysis.SessionStore>();
        services.TryAddSingleton<SessionEscalationService>();
        services.AddSingleton<IContributingDetector, SessionVectorContributor>();
        // Periodicity detection - temporal pattern analysis. Restored 2026-05-19 after
        // the 2026-05-08 retirement (commit c43f084): the SessionVector frequency
        // encoder runs internally but never exposes the periodicity.* signal surface
        // that the dashboard and policy transitions consume. Critical for the
        // API-key-theft case (sudden cadence change on the same signature).
        services.AddSingleton<IContributingDetector, PeriodicityContributor>();
        // Identity-change risk indicator - surface-dim shift on a matched fingerprint.
        // FOSS stub for the commercial API-protection feature: writes risk.* signals
        // and a low-confidence indicator contribution. FOSS policy does not gate on
        // the thresholds; commercial layers alerting / blocking on top.
        services.TryAddSingleton<FingerprintDimSnapshotCache>();
        services.AddSingleton<IContributingDetector, IdentityChangeContributor>();
        // Reactive pattern detection - post-error client behavior (backoff, compliance, coordinated retry)
        services.TryAddSingleton<ReactiveSignalTracker>();
        services.AddSingleton<IContributingDetector, ReactivePatternContributor>();
        // Claimed identity - UA family behavioral consistency via centroid matching
        services.TryAddSingleton<Services.UaProfileStore>();
        services.AddSingleton<IContributingDetector, ClaimedIdentityContributor>();
        // Session persistence - SQLite-backed session store (replaces TimescaleDB for core product)
        // Factory registration so ISessionVectorSearch (registered later) can be injected optionally.
        services.TryAddSingleton<Data.ISessionStore>(sp =>
            new Data.SqliteSessionStore(
                sp.GetRequiredService<ILogger<Data.SqliteSessionStore>>(),
                sp.GetRequiredService<IOptions<BotDetectionOptions>>(),
                sp.GetService<ISessionVectorSearch>()));
        services.AddHostedService<Services.SignatureCoordinatorWarmupService>();
        services.AddHostedService<Services.DeploymentNormCalibrationService>();
        services.AddHostedService<Data.SessionPersistenceService>();
        // Per-request persistence (every request → SQLite, LFU sampled under load)
        services.AddSingleton<Data.RequestPersistenceService>();
        // Pipeline load sensor — tracks req/s via EMA; used by background services to self-throttle
        services.TryAddSingleton<Services.PipelineLoadSensor>();
        services.AddSingleton<Services.ILoadBandSource>(sp => sp.GetRequiredService<Services.PipelineLoadSensor>());
        services.AddSingleton<Services.LoadShedDecision>();
        // Session atomization from raw requests (background, runs every 2 min)
        services.AddHostedService<Services.SessionAtomizerService>();
        // Entity resolution - background service for merge/split/rewind analysis
        services.AddHostedService<Services.EntityResolutionService>();
        // Markov chain path learning and drift detection
        services.TryAddSingleton<Markov.MarkovTracker>();
        services.TryAddSingleton<Clustering.AdaptiveSimilarityWeighter>();
        services.AddHostedService<Markov.PopulationMarkovService>();

        // Bot cluster detection - discovers bot products and coordinated campaigns
        services.TryAddSingleton<CountryReputationTracker>();
        services.TryAddSingleton<SqliteClusterStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<SqliteClusterStore>>();
            var dbPath = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value.DatabasePath
                ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");
            var basePath = Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
            var connStr = $"Data Source={Path.Combine(basePath, "clusters.db")};Cache=Shared";
            return new SqliteClusterStore(connStr, logger);
        });
        services.TryAddSingleton<BotClusterService>();
        services.AddHostedService(sp => sp.GetRequiredService<BotClusterService>());
        // Expose the read-only slice so the dashboard / REST endpoints resolve via interface
        // (remote-mode hosts substitute a HTTP-backed impl).
        services.TryAddSingleton<IBotClusterReader>(sp => sp.GetRequiredService<BotClusterService>());
        // Signature convergence - merges/splits related signatures (same IP, rotating UAs)
        services.TryAddSingleton<SignatureConvergenceService>();
        services.AddHostedService(sp => sp.GetRequiredService<SignatureConvergenceService>());
        services.AddSingleton<IContributingDetector, ClusterContributor>();

        // Content sequence detection — Priority 4, runs before all other detectors
        services.TryAddSingleton<SequenceContextStore>();
        services.AddSingleton(sp =>
        {
            var connStr = CentroidConnStr(sp);
            var logger = sp.GetRequiredService<ILogger<CentroidSequenceStore>>();
            var sessionStore = sp.GetService<ISessionStore>();

            CentroidSequenceStore.ClusterSessionLoader? loader = null;
            if (sessionStore is SqliteSessionStore sqliteSessions)
            {
                loader = async (signatures, perSig, ct) =>
                {
                    var result = new List<SessionTransitionData>();
                    if (signatures.Count == 0)
                    {
                        // Broad sample for learned-global baseline: recent confirmed-human sessions.
                        var recent = await sqliteSessions.GetRecentSessionsAsync(limit: perSig, isBot: false, ct: ct);
                        foreach (var s in recent)
                        {
                            var transitions = SessionChainAggregator.ParseTransitionCounts(s.TransitionCountsJson ?? "");
                            var dominant = SessionChainAggregator.ParseDominantState(s.DominantState);
                            if (transitions.Count > 0)
                                result.Add(new SessionTransitionData(dominant, transitions));
                        }
                        return result;
                    }

                    foreach (var sig in signatures)
                    {
                        var sessions = await sqliteSessions.GetSessionsAsync(sig, perSig, ct);
                        foreach (var s in sessions)
                        {
                            var transitions = SessionChainAggregator.ParseTransitionCounts(s.TransitionCountsJson ?? "");
                            var dominant = SessionChainAggregator.ParseDominantState(s.DominantState);
                            if (transitions.Count > 0)
                                result.Add(new SessionTransitionData(dominant, transitions));
                        }
                    }
                    return result;
                };
            }

            return new CentroidSequenceStore(connStr, logger, loader);
        });
        services.TryAddSingleton<EndpointDivergenceTracker>();
        services.AddSingleton(sp =>
        {
            var connStr = CentroidConnStr(sp);
            var centroidStore = sp.GetRequiredService<CentroidSequenceStore>();
            var logger = sp.GetRequiredService<ILogger<AssetHashStore>>();
            return new AssetHashStore(connStr, centroidStore, logger);
        });
        services.AddSingleton<IContributingDetector, ContentSequenceContributor>();
        services.AddHostedService<CentroidSequenceRebuildHostedService>();
        services.AddHostedService<AssetHashInitHostedService>();
        // Constrained LLM description coordinator (KeyedSequentialAtom, 50% CPU concurrency)
        services.AddSingleton<LlmDescriptionCoordinator>();
        // LLM-based cluster descriptions (background, never in request pipeline)
        services.AddSingleton<BotClusterDescriptionService>();

        // ==========================================
        // Bot Name Synthesizer (provided by LLM plugin packages)
        // ==========================================
        // Default no-op synthesizer - replaced by Mostlylucid.BotDetection.Llm.* packages
        // Deterministic naming from signals (immediate, no LLM required).
        // LLM packages override this with richer AI-generated names when available.
        services.TryAddSingleton<IBotNameSynthesizer, DeterministicBotNameSynthesizer>();

        // ==========================================
        // Signature Description Service (Background)
        // ==========================================
        // Generates LLM descriptions for signatures once they reach request threshold.
        // Registered as singleton + hosted service so the broadcast middleware can inject it.
        services.AddSingleton<SignatureDescriptionService>();
        services.AddHostedService(sp => sp.GetRequiredService<SignatureDescriptionService>());

        // CVE fingerprint matching - runs after Heuristic (priority 55) to match traffic against CVE-derived shapes
        services.TryAddSingleton<ICveFingerprintMatcher, NullCveFingerprintMatcher>();
        services.AddSingleton<IContributingDetector, CveFingerprintContributor>();
        // Similarity search - runs after Heuristic (priority 60) to leverage feature extraction
        services.AddSingleton<IContributingDetector, SimilarityContributor>();
        // AI/LLM detectors (run when escalation triggered or in demo mode)
        services.AddSingleton<IContributingDetector, LlmContributor>();
        // Heuristic late - runs AFTER AI (or after all static if no AI), consumes all evidence
        services.AddSingleton<IContributingDetector, HeuristicLateContributor>();

        // ==========================================
        // Background Enrichment Service (async DNS lookups for ProjectHoneypot)
        // ==========================================
        services.AddSingleton<BackgroundEnrichmentService>();
        services.AddHostedService(sp => sp.GetRequiredService<BackgroundEnrichmentService>());

        // ==========================================
        // Background LLM Classification Coordinator
        // ==========================================
        services.AddSingleton<LlmClassificationCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<LlmClassificationCoordinator>());

        // ==========================================
        // Background Intent Classification Coordinator (threat scoring)
        // ==========================================
        services.AddSingleton<IntentClassificationCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<IntentClassificationCoordinator>());

        // ==========================================
        // Similarity Search (Slim* bounded in-memory, SQLite-backed centroids)
        // ==========================================

        // Feature vectorizer converts dynamic feature dictionaries to fixed-length vectors
        services.TryAddSingleton<FeatureVectorizer>();

        // Intent vectorizer (threat scoring via session intent patterns)
        services.TryAddSingleton<IntentVectorizer>();

        // SQLite centroid stores - share the same DB file as the session store.
        // Compute the path directly from BotDetectionOptions (same logic as SqliteSessionStore)
        // to avoid a circular dependency: ISessionStore → ISessionVectorSearch → ISessionCentroidStore → ISessionStore.
        static string CentroidConnStr(IServiceProvider sp)
        {
            var dbPath = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value.DatabasePath
                ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");
            var basePath = Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
            return $"Data Source={Path.Combine(basePath, "sessions.db")};Cache=Shared";
        }
        services.TryAddSingleton<ISignatureCentroidStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Data.SqliteSignatureCentroidStore>>();
            return new Data.SqliteSignatureCentroidStore(CentroidConnStr(sp), logger);
        });
        services.TryAddSingleton<ISessionCentroidStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Data.SqliteSessionCentroidStore>>();
            return new Data.SqliteSessionCentroidStore(CentroidConnStr(sp), logger);
        });
        services.TryAddSingleton<IIntentCentroidStore>(sp =>
        {
            var logger = sp.GetRequiredService<ILogger<Data.SqliteIntentCentroidStore>>();
            return new Data.SqliteIntentCentroidStore(CentroidConnStr(sp), logger);
        });

        // Slim* bounded in-memory similarity search backed by SQLite centroids.
        // Replaces unbounded HNSW graphs - no file I/O on the hot path, bounded LOH.
        services.TryAddSingleton<ISignatureSimilaritySearch, SlimSignatureSimilaritySearch>();
        services.TryAddSingleton<ISessionVectorSearch, SlimSessionVectorSearch>();
        services.TryAddSingleton<IIntentSimilaritySearch, SlimIntentSearch>();

        // Warmup: seeds the Slim* caches from SQLite centroids on first startup
        services.AddHostedService<Services.SessionVectorWarmupService>();
        // Nightly compaction: SQLite session compaction + centroid pruning
        services.AddHostedService<Services.VectorCompactionService>();

        // Learning handler that feeds high-confidence detections into the similarity index
        services.AddSingleton<ILearningEventHandler, SimilarityLearningHandler>();

        // Learning handler that feeds intent classifications into the intent HNSW index
        services.AddSingleton<ILearningEventHandler, IntentLearningHandler>();

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

        // Challenge store (PoW challenge issuance + verification feedback loop)
        // SQLite for FOSS (zero-dependency). Commercial overrides with PostgreSQL or Redis.
        services.TryAddSingleton<IChallengeStore, SqliteChallengeStore>();

        // Fingerprint approval store (SQLite-backed, locked dimensions, audit trail)
        services.TryAddSingleton<IFingerprintApprovalStore, SqliteFingerprintApprovalStore>();

        // Fingerprint approval contributor (checks approval + locked dimensions)
        services.AddSingleton<IContributingDetector, FingerprintApprovalContributor>();

        // Fingerprint prior contributor (Wave 0: injects cached verdict as bias)
        services.AddSingleton<IContributingDetector, FingerprintPriorContributor>();

        // Challenge verification contributor (reads PoW solve metadata as detection signal)
        services.AddSingleton<IContributingDetector, ChallengeVerificationContributor>();

        // Register simulation pack responder (serves fake responses for honeypot paths)
        services.AddSingleton<SimulationPackResponder>();
        services.AddSingleton<IActionPolicy>(sp => sp.GetRequiredService<SimulationPackResponder>());

        // Register action policy factories (create policies from configuration)
        services.AddSingleton<IActionPolicyFactory, BlockActionPolicyFactory>();
        services.AddSingleton<IActionPolicyFactory, ThrottleActionPolicyFactory>();
        services.AddSingleton<IActionPolicyFactory, ChallengeActionPolicyFactory>();
        services.AddSingleton<IActionPolicyFactory, RedirectActionPolicyFactory>();
        services.AddSingleton<IActionPolicyFactory, LogOnlyActionPolicyFactory>();

        // Register action policy registry (holds named action policies)
        services.TryAddSingleton<IActionPolicyRegistry, ActionPolicyRegistry>();

        // ==========================================
        // Compliance packs
        // ==========================================
        services.TryAddSingleton<ICompliancePackProvider>(sp =>
        {
            var configuration = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            // Indexer instead of GetValue<string> - the indexer is trim-safe; GetValue<T>
            // triggers IL2026 because it would have to reflect over T's members for non-primitive
            // types. For a plain string key the difference is academic, but it keeps the AOT
            // publish clean of the only remaining own-code IL warning.
            var compliancePackId = configuration?["BotDetection:CompliancePack"] ?? "balanced-default";
            return new InMemoryCompliancePackProvider(
                compliancePackId,
                sp.GetRequiredService<ILogger<InMemoryCompliancePackProvider>>());
        });
    }

    /// <summary>
    ///     Add OpenTelemetry instrumentation for bot detection.
    ///     Automatically exports detection signals as spans, metrics, and span events.
    ///     Wire up the OTel SDK in the host application to consume these.
    /// </summary>
    /// <example>
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddBotDetectionTelemetry(opts =&gt; {
    ///         opts.EnableMetrics = true;
    ///         opts.EnableTracing = true;
    ///         opts.EnableScoreJourney = true;
    ///     });
    ///
    ///     // Then in OTel SDK setup:
    ///     builder.Services.AddOpenTelemetry()
    ///         .WithMetrics(m =&gt; m.AddMeter("Mostlylucid.BotDetection.Signals"))
    ///         .WithTracing(t =&gt; t.AddSource("Mostlylucid.BotDetection"));
    /// </example>
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
}
