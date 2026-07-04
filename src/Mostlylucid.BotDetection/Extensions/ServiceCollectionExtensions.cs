using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
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
using Mostlylucid.BotDetection.Definitions.TlsReference;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.Detectors;
using Mostlylucid.BotDetection.Identity;
// LlmDetector removed - now in Mostlylucid.BotDetection.Llm.Ollama/LlamaSharp packages
using Mostlylucid.BotDetection.Events;
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
using Mostlylucid.BotDetection.Policies.Dispatch;
using Mostlylucid.BotDetection.Privacy;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.BotDetection.Compliance;
using Mostlylucid.BotDetection.Proxy;
using Mostlylucid.BotDetection.Services.Llm;
using Mostlylucid.BotDetection.Setup;
using Mostlylucid.BotDetection.SimulationPacks;
using Mostlylucid.Atoms.Ephemeral;
using Mostlylucid.Common.Scheduling;

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
            .PostConfigure(options =>
            {
                // The regex match timeout is captured by Regex.Compiled at compile
                // time and binds with the rule, so configure the parser BEFORE the
                // static Default is touched (any UserAgentParser.Parse call will
                // trigger the compile). PostConfigure runs at options finalisation,
                // which happens during host build before request processing starts.
                Helpers.UapCoreUserAgentParser.ConfigureRegexTimeout(
                    TimeSpan.FromMilliseconds(options.UserAgents.RegexMatchTimeoutMs));
            })
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
    ///     Add bot detection in <b>ephemeral mode</b>: same detector pipeline,
    ///     no SQLite. Every store that would normally write to disk is replaced
    ///     with a no-op or in-process implementation, so detection runs against
    ///     the in-process LFU / bounded caches only and all state evaporates on
    ///     restart.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         Use this when you need quick protection but cannot have persistence:
    ///         serverless / scratch containers, demo sandboxes, integration tests
    ///         that need fresh state per run. Intentionally weaker than the
    ///         default SQLite-backed mode -- learning, entity resolution, the
    ///         metastable identity layer and the verdict cache all rebuild from
    ///         zero every restart.
    ///     </para>
    ///     <para>
    ///         Identity is forced off (it relies on persisted centroids; with no
    ///         persistence the matcher is purely degraded). Per-request signal
    ///         detection runs unchanged.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddBotDetectionInMemory(
        this IServiceCollection services,
        Action<BotDetectionOptions>? configure = null)
    {
        services.AddBotDetection(options =>
        {
            // Identity layer relies on persisted centroids + per-fingerprint
            // weights. With no persistence it can only degrade -- force off so
            // the absorption/drift/calibration hosted services stay dormant.
            options.Identity.Enabled = false;
            configure?.Invoke(options);
        });

        // Per-store Null/InMemory bindings. services.Replace() swaps the SQLite
        // descriptor put in place by AddBotDetection; the concrete SQLite
        // implementations stay in the container (some are still resolved by
        // name) but no I/O happens through this binding.
        services.Replace(ServiceDescriptor.Singleton<Identity.IFingerprintStore, Identity.NullFingerprintStore>());
        services.Replace(ServiceDescriptor.Singleton<Identity.IFingerprintReader>(
            sp => (Identity.NullFingerprintStore)sp.GetRequiredService<Identity.IFingerprintStore>()));
        services.Replace(ServiceDescriptor.Singleton<ISessionStore, NullSessionStore>());
        services.Replace(ServiceDescriptor.Singleton<IClusterStore, NullClusterStore>());
        services.Replace(ServiceDescriptor.Singleton<ILearnedPatternStore, NullLearnedPatternStore>());
        services.Replace(ServiceDescriptor.Singleton<IWeightStore, NullWeightStore>());
        services.Replace(ServiceDescriptor.Singleton<Lifecycle.IPathLifecycleStore, Lifecycle.NullPathLifecycleStore>());
        services.Replace(ServiceDescriptor.Singleton<IChallengeStore, InMemoryChallengeStore>());
        services.Replace(ServiceDescriptor.Singleton<IPinnedEndpointStore, NullPinnedEndpointStore>());
        services.Replace(ServiceDescriptor.Singleton<IFingerprintApprovalStore, NullFingerprintApprovalStore>());
        // Centroid stores open sessions.db directly via their own connection-string
        // factory, so a Replace on ISessionStore alone leaves sessions.db being created.
        services.Replace(ServiceDescriptor.Singleton<ISignatureCentroidStore, NullSignatureCentroidStore>());
        services.Replace(ServiceDescriptor.Singleton<ISessionCentroidStore, NullSessionCentroidStore>());
        services.Replace(ServiceDescriptor.Singleton<IIntentCentroidStore, NullIntentCentroidStore>());

        // Bot signature catalog: the SQLite-backed BotListDatabase writes the
        // pattern + datacenter-IP cache to botdetection.db. In economy mode we
        // hold both lists in memory only via InMemoryBotListDatabase -- same
        // IBotListFetcher source data, no SQLite, no file on disk.
        services.Replace(ServiceDescriptor.Singleton<IBotListDatabase>(sp =>
            new InMemoryBotListDatabase(
                sp.GetRequiredService<IBotListFetcher>(),
                sp.GetRequiredService<ILogger<InMemoryBotListDatabase>>())));

        // CentroidSequenceStore + AssetHashStore are concrete-typed singletons with
        // no interface to Replace; they open sessions.db directly in their hosted
        // services' init paths. Pull them out of the container entirely along with
        // their hosted services, the ContentSequenceContributor that depends on
        // them, and the AssetHashInitHostedService. ephemeral mode silently
        // degrades content-sequence detection -- documented in economy-mode.md.
        services.RemoveAll<Services.CentroidSequenceStore>();
        services.RemoveAll<Services.AssetHashStore>();
        services.RemoveAll<Services.EndpointDivergenceTracker>();
        RemoveHostedService<Services.CentroidSequenceRebuildHostedService>(services);
        RemoveHostedService<Services.AssetHashInitHostedService>(services);
        RemoveContributor<Orchestration.ContributingDetectors.ContentSequenceContributor>(services);

        return services;
    }

    /// <summary>
    ///     Helper: remove an <see cref="Microsoft.Extensions.Hosting.IHostedService"/>
    ///     registered as <typeparamref name="THosted"/>. <c>RemoveAll&lt;IHostedService&gt;</c>
    ///     would nuke every hosted service in the container; this only removes the
    ///     specific implementation type.
    /// </summary>
    private static void RemoveHostedService<THosted>(IServiceCollection services)
        where THosted : Microsoft.Extensions.Hosting.IHostedService
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var sd = services[i];
            if (sd.ServiceType != typeof(Microsoft.Extensions.Hosting.IHostedService)) continue;
            if (sd.ImplementationType == typeof(THosted)
                || (sd.ImplementationFactory is not null && sd.ImplementationFactory.Method.ReturnType == typeof(THosted)))
            {
                services.RemoveAt(i);
            }
        }
    }

    /// <summary>
    ///     Helper: remove an <see cref="IContributingDetector"/> registered as
    ///     <typeparamref name="TContrib"/>. Contributors are registered as
    ///     <c>AddSingleton&lt;IContributingDetector, TContrib&gt;()</c> so
    ///     <c>RemoveAll&lt;IContributingDetector&gt;</c> would nuke the whole pipeline.
    /// </summary>
    private static void RemoveContributor<TContrib>(IServiceCollection services)
        where TContrib : IContributingDetector
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            var sd = services[i];
            if (sd.ServiceType != typeof(IContributingDetector)) continue;
            if (sd.ImplementationType == typeof(TContrib))
                services.RemoveAt(i);
        }
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
        // Domain normalization -- canonical (Host, ETldPlusOne) resolution for the
        // multi-domain storage layer. PublicSuffixList is loaded once from the
        // embedded IANA data; DomainNormalizer is stateless and cheap to share.
        // Options bind from BotDetection:DomainNormalizer (CustomPublicSuffixes /
        // TreatWwwAsCanonical). Kept as TryAdd so a host embedding a custom PSL or
        // normalizer (e.g. an internal-domain-only fixture) can Replace() first.
        services.AddOptions<DomainNormalizerOptions>()
            .BindConfiguration(DomainNormalizerOptions.SectionName);
        services.TryAddSingleton(_ => PublicSuffixList.LoadEmbedded());
        services.TryAddSingleton<DomainNormalizer>();

        // Rate-limiting primitives -- in-memory token bucket + leaky-bucket data
        // stream. Commercial replaces IRateLimitStateStore with a Redis-backed
        // implementation via TryAdd so a multi-gateway cluster shares budgets.
        // The high-level limiter and data-rate wrapper stay FOSS regardless.
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimiting.IRateLimitStateStore,
                                  Mostlylucid.BotDetection.RateLimiting.MemoryRateLimitStateStore>();
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimiting.IRateLimiter,
                                  Mostlylucid.BotDetection.RateLimiting.MemoryRateLimiter>();
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimiting.IDataRateLimiter,
                                  Mostlylucid.BotDetection.RateLimiting.MemoryDataRateLimiter>();
        // Subject resolver -- IPinnedLabelLookup and IRegionMap are optional; the
        // resolver tolerates nulls. The UI layer registers a label-lookup adapter
        // separately; FOSS apps without that ship with no PinnedLabel matching,
        // which is the correct behaviour (no label store, no labels to match).
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimiting.RateLimitSubjectResolver>();

        // Scope-tree resolver -- reads RateLimitOptions and walks the full
        // scope chain (global / domain / subdomain / endpoint / method).
        // Detection is symmetric across FOSS and commercial; commercial may
        // still override for extended concerns (e.g. tenant-scoped dynamic
        // reload) but the default walk here handles the full tree.
        services.AddOptions<Mostlylucid.BotDetection.RateLimiting.RateLimitOptions>()
            .BindConfiguration(Mostlylucid.BotDetection.RateLimiting.RateLimitOptions.SectionName);
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimiting.ScopedRateLimitResolver>();

        // The enforcer is what DetectionPolicyMiddleware (phase 5) calls. It
        // glues the four primitives together: subjects + rules + buckets +
        // data-rate-wrap + over-limit dispatch via the existing action
        // registry. Singleton; state lives in the underlying state store.
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimiting.RateLimitEnforcer>();

        // Detection-policy gate (post-detection rule matching with the four-
        // axis predicate set: BotProbability / Confidence / Type / Threat).
        // The middleware itself is registered by the host's UseBotDetection /
        // UseDetectionPolicies hook (see UseDetectionPolicies extension).
        services.AddOptions<Mostlylucid.BotDetection.EndpointPolicies.DetectionPolicyOptions>()
            .BindConfiguration(Mostlylucid.BotDetection.EndpointPolicies.DetectionPolicyOptions.SectionName);

        // Add HttpClient factory for bot list fetching
        services.AddHttpClient();

        // Named HttpClient for VerifiedBotContributor (fetches published IP range lists)
        services.AddHttpClient("VerifiedBot", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("StyloBot/1.0 (+https://stylo.bot; stylobot@mostlylucid.net)");
        });

        // VerifiedBotRegistry options - configurable via appsettings.json: BotDetection:VerifiedBotRegistry
        services.AddOptions<VerifiedBotRegistryOptions>()
            .BindConfiguration("BotDetection:VerifiedBotRegistry");

        // Proxy topology sensing: auto-detects the CDN/proxy in front of the app and
        // resolves the real client IP from the correct headers (CF-Connecting-IP, X-Real-IP, etc.).
        // Registered as singleton - topology is detected once on first request and cached.
        services.TryAddSingleton<IProxyEnvironment, ProxyEnvironmentDetector>();
        services.TryAddSingleton<ITransportHeaderTrust, TransportHeaderTrust>();

        // Startup-time tunnel-enrichment inspector: observes the first N requests, decides
        // whether the gateway sees TLS / JA3 (native or forwarded), and snapshots the verdict
        // for the dashboard to render an actionable banner. No continuous polling, no
        // per-request cost once settled.
        services.TryAddSingleton<ITunnelEnvironmentInspector, TunnelEnvironmentInspector>();

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

        // ScheduleCoordinator -- the canonical tick.* signal source. Wave 1 of
        // the architectural-drift remediation; Wave 2 will migrate
        // MeterTriggerService / LocalMeterStream eviction / RemoteMeterStream
        // poll off BackgroundService and onto Subscribe(TickCadence.*).
        //
        // This IS the project's one allowed IHostedService -- per
        // feedback_no_background_services in user memory. TryAdd so a host that
        // wants a different scheduler can Replace() the singleton before the
        // hosted-service descriptor resolves it.
        services.AddOptions<Scheduling.ScheduleCoordinatorOptions>()
            .BindConfiguration(Scheduling.ScheduleCoordinatorOptions.SectionName);
        services.TryAddSingleton<Scheduling.ScheduleCoordinator>();
        services.TryAddSingleton<Mostlylucid.Common.Scheduling.IScheduleCoordinator>(
            sp => sp.GetRequiredService<Scheduling.ScheduleCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<Scheduling.ScheduleCoordinator>());

        // ScheduleCoordinatorWatchdog: the irreducible bootstrap watchdog. See
        // the class comment for why this ONE BackgroundService is justified.
        services.AddHostedService<Scheduling.ScheduleCoordinatorWatchdog>();

        // Meter-signals extension point (IMeterSignalSink / NullMeterSignalSink)
        // lives in Mostlylucid.BotDetection.PrometheusPack now -- AddLocalMeterStream
        // / AddRemoteMeterStream register the default null sink there.

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
        // Register bot list update service.
        // Wave 2: migrated to ScheduleCoordinator tick.1h. Eager-resolved by
        // BotDetectionHostedSingletonsBootstrap so the constructor's Subscribe(...)
        // fires at boot.
        services.AddSingleton<BotListUpdateService>();

        // Single-shot startup hook that drives demo presets through the
        // orchestrator if BotDetection:DemoPreloadOnStartup is set. No-op when
        // the list is empty or EnableTestMode is false.
        services.AddHostedService<Mostlylucid.BotDetection.Middleware.DemoPreloadHostedService>();

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
        // Wave 2 migrated: BoundedChannelLearningBus subscribes to
        // ScheduleCoordinator.Tick1s at construction (HP-mode front-end
        // channel drains to inner bus via TryRead per tick). Plain singleton
        // registration; BotDetectionHostedSingletonsBootstrap eagerly resolves
        // it at boot so the subscription is live before the first event lands.
        services.TryAddSingleton<BoundedChannelLearningBus>();
        services.TryAddSingleton<ILearningEventBus>(sp => sp.GetRequiredService<BoundedChannelLearningBus>());

        // Register learning event handlers
        services.AddSingleton<ILearningEventHandler, InferenceHandler>();
        services.AddSingleton<ILearningEventHandler, PatternAccumulatorHandler>();
        services.AddSingleton<ILearningEventHandler, FeedbackHandler>();
        services.AddSingleton<ILearningEventHandler, DriftDetectionHandler>();

        // Wave 2 migrated: LearningBackgroundService subscribes to
        // ScheduleCoordinator.Tick1s at construction (per-tick TryRead drain
        // of ILearningEventBus.Reader). Plain singleton registration;
        // BotDetectionHostedSingletonsBootstrap eagerly resolves it at boot
        // so the subscription is live before the first event lands.
        services.TryAddSingleton<LearningBackgroundService>();

        // Register fast-path decider for UA short-circuit with sampling
        services.TryAddSingleton<FastPathDecider>();

        // Register learned pattern store (SQLite-backed)
        services.TryAddSingleton<ILearnedPatternStore, SqliteLearnedPatternStore>();

        // Register weight store for learning feedback loop
        services.TryAddSingleton<IWeightStore, SqliteWeightStore>();

        // Register signature feedback handler (feeds learned patterns back to detectors)
        services.AddSingleton<ILearningEventHandler, SignatureFeedbackHandler>();

        // Register common user agent service (scrapes useragents.me for browser versions and common UAs).
        // Wave 2 migrated: subscribes to ScheduleCoordinator.Tick1h at
        // construction. BotDetectionHostedSingletonsBootstrap eagerly
        // resolves it at boot so the subscription is live before the
        // first tick.
        services.TryAddSingleton<ICommonUserAgentService, CommonUserAgentService>();
        services.TryAddSingleton<IBrowserVersionService>(sp =>
            (CommonUserAgentService)sp.GetRequiredService<ICommonUserAgentService>());

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
        // Wave 2: drop AddHostedService -- the registry subscribes to
        // IScheduleCoordinator.Tick1h at construction for IP-range refresh.
        // The BotDetectionHostedSingletonsBootstrap shim forces resolution at
        // boot so the subscription is not dormant until first
        // request-time resolution. See the migration plan dated 2026-06-15.
        services.TryAddSingleton<VerifiedBotRegistry>();
        services.AddSingleton<IContributingDetector, VerifiedBotContributor>();
        // Inline IP-range verifier (priority 4) - catches UA-claim impersonators on
        // the first request when the claimed bot publishes CIDR ranges (Bingbot,
        // Googlebot, Amazonbot, OpenAI's GPTBot, etc.). The full VerifiedBotContributor
        // does rDNS too and is too slow inline; this one skips DNS and only checks
        // IP ranges (O(n) CIDR lookup, zero I/O). Live trigger: sig 9z3avO7sKTd7NAYY896Yog
        // claimed Amazonbot from a Hong Kong residential IP.
        services.AddSingleton<IContributingDetector, VerifiedBotInlineContributor>();
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
            client.DefaultRequestHeaders.UserAgent.ParseAdd("stylobot-nodeinfo-verifier/1.0 (+https://stylo.bot)");
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            AllowAutoRedirect = false,  // SSRF guard -- no redirect chains
            UseCookies = false
        });
        // Forward-DNS resolver for FediverseDomainContributor's IP-side
        // confirmation (Gap #3 from the 2026-06-15 claim-verify-trust gap
        // analysis): NodeInfo confirms the claimed instance domain hosts
        // ActivityPub software but never binds the client IP to the claim.
        // SystemDnsResolver wraps Dns.GetHostAddressesAsync; swap for tests via
        // IDnsResolver. Singleton -- the resolver is stateless and the
        // contributor caches results in BoundedCache anyway.
        services.TryAddSingleton<IDnsResolver, SystemDnsResolver>();
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
        // Wave 2 (Category B): dropped BackgroundService inheritance and now
        // subscribes to ScheduleCoordinator Tick10s at construction.
        // BotDetectionHostedSingletonsBootstrap eagerly resolves it at boot so
        // the subscription is live before the first tick.
        services.AddSingleton<ThreatIntel.ThreatIntelEnrichmentService>();
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
        // Time-of-day facets: emits time.hour_of_day / time.day_of_week /
        // time.is_weekend / time.is_business_hours so DSL rules can key on
        // "off-hours" without a separate clock-aware predicate (priority 5).
        // BotDetection:Time section controls TimeZone + business-hours window.
        services.AddOptions<Models.TimeOptions>().BindConfiguration("BotDetection:Time");
        // TimeContributor ctor takes TimeProvider; the test harness and slim
        // hosts don't register it implicitly. TryAdd so a host that wants a
        // FakeTimeProvider can override BEFORE calling AddStyloBotBotDetection.
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        services.AddSingleton<IContributingDetector, TimeContributor>();
        // PII query string detection - privacy signals, not bot detection (priority 8)
        services.AddSingleton<IContributingDetector, PiiQueryStringContributor>();
        services.AddSingleton<IContributingDetector, UserAgentContributor>();
        // Identity (metastable fingerprint match). See docs/architecture/fingerprint-match.md.
        // Both contributors are foundation, dormant when Identity.Enabled = false.
        services.TryAddSingleton(sp => Identity.IdentityVectorLayout.DefaultV1());
        services.TryAddSingleton<Identity.IdentityVectorEncoder>();
        // Sub-resource amortisation cache. IdentityVectorContributor short-circuits its
        // Encode call when this hands back a live cached vector for the request's
        // primary_signature. Dormant when Identity.Enabled = false or
        // BotDetection:Identity:Vector:EncoderCacheEnabled = false.
        services.TryAddSingleton<Identity.EncoderResultCache>();
        // Adaptive trigger signal source for calibration. Singleton so the
        // store + absorption service + calibration service all share the same
        // counters. Always registered (cheap) even when Identity is off --
        // the legacy Tick1m gate ignores it; the adaptive trigger consults
        // it only when explicitly enabled in BotDetection:Identity:Calibration:Trigger.
        services.TryAddSingleton<Identity.Triggers.CalibrationSignalSource>();
        services.TryAddSingleton<Identity.Triggers.IAdaptiveTriggerSignalSource>(
            sp => sp.GetRequiredService<Identity.Triggers.CalibrationSignalSource>());
        services.TryAddSingleton<Identity.SqliteFingerprintStore>();
        // Surface the read-only fingerprint interface so the dashboard / REST endpoints
        // resolve it without depending on the concrete store - swapped for a HTTP-backed
        // impl in remote-mode dashboard hosts.
        services.TryAddSingleton<Identity.IFingerprintReader>(
            sp => sp.GetRequiredService<Identity.SqliteFingerprintStore>());
        // Full read+write fingerprint surface for the detection pipeline. Consumers
        // depend on this interface (not the concrete store) so commercial gateways
        // can swap in a Postgres-backed implementation. Defaults to the Sqlite store.
        services.TryAddSingleton<Identity.IFingerprintStore>(
            sp => sp.GetRequiredService<Identity.SqliteFingerprintStore>());
        // HR2 dashboard live-update beacon for fingerprint name-slot edits. NoOp
        // default keeps the commercial editor + Redis subscriber free of null
        // guards on lightweight viewer hosts and test fixtures; AddStyloBotDashboard
        // replaces this with a SignalR-backed broadcaster on hosts that mount the
        // dashboard surface.
        services.TryAddSingleton<Identity.IFingerprintDirtyBroadcaster,
            Identity.NoOpFingerprintDirtyBroadcaster>();
        // Read-only entity-resolution surface. Local impl wraps the session store
        // (which owns the writes); remote-mode dashboards swap this for
        // RemoteEntityReader proxying /api/v1/entities/*.
        services.TryAddSingleton<Data.IEntityReader, Data.LocalEntityReader>();
        // Anchor index: vec0 wrapper that dispatches to brute force when sqlite-vec didn't
        // load. Both impls registered as concrete types so the wrapper can fall back per-call.
        services.TryAddSingleton<Identity.BruteForceIdentityAnchorIndex>();
        services.TryAddSingleton<Identity.IIdentityAnchorIndex, Identity.SqliteVecIdentityAnchorIndex>();
        services.TryAddSingleton<Identity.IdentityArchetypeRegistry>();
        // Wave 2: migrated to ScheduleCoordinator tick.10s. Eager-resolved by
        // BotDetectionHostedSingletonsBootstrap so the constructor's Subscribe(...)
        // fires at boot.
        services.AddSingleton<Identity.IdentityGlobalWeightsCache>();
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
        // Browser-mode classifier: same browser, different modes (navigation/xhr/
        // sub-resource/signalr-negotiate/...). Foundation contributor priority
        // 6, emits identity.browser_mode (+ identity.browser_mode_similarity)
        // signal. See docs/architecture/composite-character-fingerprints.md
        // and the 2026-06-22 identity / mode / archetype / name design spec.
        //
        // The legacy YAML-predicate BrowserModeRegistry is still registered
        // (other code paths -- registry inventory dumps, predicate-shape
        // tests -- still touch it) but is NOT on the request-scoped
        // classification path any more. T14 retired the predicate walk in
        // favour of nearest-centroid cosine over the browser_mode catalogue
        // (catalogue_kind = 'browser_mode' rows in identity_archetypes), so
        // calibration drift survives gateway restart. See
        // feedback_centroids_not_rules.
        services.TryAddSingleton<Identity.BrowserModes.BrowserModeRegistry>(sp =>
            new Identity.BrowserModes.BrowserModeRegistry(
                sp.GetRequiredService<ILogger<Identity.BrowserModes.BrowserModeRegistry>>(),
                sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value.Identity.BrowserMode.FallbackModeId));

        // Mode-centroid catalogue + classifier (T11b / T12 / T13 / T14).
        // YamlBrowserModeSeedSource pulls cold-start seeds from
        // Definitions/BrowserModes/*.yaml; ModeCentroidCatalogue persists
        // each seed into identity_archetypes on first read and surfaces drift
        // updates on subsequent reads. ModeCentroidClassifier snapshots the
        // catalogue into an in-memory nearest-centroid scanner at startup.
        // GetAwaiter().GetResult() at DI build time is acceptable here: the
        // load is read-only and bounded by the small browser_mode row count.
        services.TryAddSingleton<Identity.BrowserModes.IBrowserModeSeedSource>(sp =>
            new Identity.BrowserModes.YamlBrowserModeSeedSource(
                sp.GetRequiredService<Identity.IdentityVectorLayout>(),
                sp.GetService<ILogger<Identity.BrowserModes.YamlBrowserModeSeedSource>>()));
        services.TryAddSingleton<Identity.BrowserModes.ModeCentroidCatalogue>();
        services.TryAddSingleton<Identity.BrowserModes.ModeCentroidClassifier>(sp =>
        {
            var catalogue = sp.GetRequiredService<Identity.BrowserModes.ModeCentroidCatalogue>();
            var layout    = sp.GetRequiredService<Identity.IdentityVectorLayout>();
            return Identity.BrowserModes.ModeCentroidClassifier
                .LoadAsync(catalogue, layout)
                .GetAwaiter().GetResult();
        });

        // Request-scoped BrowserMode resolver: lazy-classifies on first call,
        // caches in HttpContext.Items. EndpointPolicy (early in the pipeline)
        // and BrowserModeClassifierContributor / FingerprintMatchContributor
        // (mid-pipeline, after BotDetection wave starts) all consult the same
        // resolver, so a request is classified at most once. Production impl
        // is CentroidBrowserModeResolver (cosine over the browser_mode
        // catalogue); the YAML-predicate CachingBrowserModeResolver is kept
        // for callers that explicitly construct it (tests).
        services.TryAddSingleton<Identity.BrowserModes.IBrowserModeResolver,
            Identity.BrowserModes.CentroidBrowserModeResolver>();
        services.AddSingleton<IContributingDetector, BrowserModeClassifierContributor>();
        // Per-fingerprint browser-mode store (step 2 of the composite spec).
        // Surface the interface so commercial Postgres can swap in its impl;
        // FOSS default is the SQLite-backed store sharing the fingerprints.db
        // connection. Read-side is LFU-cached, writes invalidate per-fingerprint.
        services.TryAddSingleton<Identity.BrowserModes.SqliteFingerprintBrowserModeStore>();
        services.TryAddSingleton<Identity.BrowserModes.IFingerprintBrowserModeStore>(
            sp => sp.GetRequiredService<Identity.BrowserModes.SqliteFingerprintBrowserModeStore>());

        // Wave 2 Cat-C* (paired wave window): mode-observation drainer + rollup
        // recompute + parent absorption all subscribe to IScheduleCoordinator.Tick5m
        // at construction. Drop the AddHostedService trampolines;
        // BotDetectionHostedSingletonsBootstrap eagerly resolves the singletons at
        // boot so the constructor's Subscribe(...) fires before the first tick. The
        // three services land on the same wave window so a rollup recompute sees
        // consistent parent + per-mode state per project_absorption_services_migration.
        services.AddSingleton<Identity.BrowserModes.FingerprintModeAbsorptionService>();
        services.AddSingleton<Identity.BrowserModes.FingerprintRollupRecomputeService>();
        services.AddSingleton<IContributingDetector, FingerprintMatchContributor>();
        // Spec D2 (deferred -- T14 follow-up): FingerprintNameComposer should
        // recompute the display name as a SignalSink on FingerprintMatched /
        // MatcherCatalogUpdated events so a centroid-drift or archetype
        // recompose updates the persisted name without a manual call site.
        // Neither the typed FingerprintMatched / MatcherCatalogUpdated signal
        // nor a FOSS-side ISignalSink pub-sub bus exists yet (the
        // Mostlylucid.Ephemeral SignalSink is a request-scoped ledger, not a
        // pub-sub bus), so wiring the subscriber lands in a later task
        // (identity-plan T16+). Today the composer stays statically invoked
        // by the matcher + LLM callback + IFingerprintStore display-name
        // gate. Per feedback_no_background_services, when this lands it MUST
        // NOT be a HostedService -- it subscribes to the signal flow.
        services.AddSingleton<Identity.FingerprintAbsorptionService>();
        // Wave 2: FingerprintDriftService subscribes to
        // IScheduleCoordinator.Tick10s at construction. Drop the
        // AddHostedService trampoline; BotDetectionHostedSingletonsBootstrap
        // eagerly resolves the singleton at boot so the subscription is
        // not dormant until first request-time resolution.
        services.AddSingleton<Identity.FingerprintDriftService>();
        // Wave 2: migrated to ScheduleCoordinator tick.1m. Eager-resolved by
        // BotDetectionHostedSingletonsBootstrap so the constructor's Subscribe(...)
        // fires at boot.
        services.AddSingleton<Identity.IdentityWeightCalibrationService>();
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
        // Per-host site profiles modulate the honeypot exempt list + Tier 1/2
        // promotions. Catalog loads embedded YAMLs; resolver compiles the
        // host map at startup. Both optional from the consumer's view.
        services.AddOptions<SiteProfiles.SiteMapOptions>()
            .BindConfiguration(SiteProfiles.SiteMapOptions.SectionName);
        services.TryAddSingleton<SiteProfiles.ISiteProfileCatalog, SiteProfiles.SiteProfileCatalog>();
        services.TryAddSingleton<SiteProfiles.ISiteProfileResolver, SiteProfiles.SiteProfileResolver>();
        // Per-request effective threshold overlay: global → domain-profile →
        // host-profile with per-field null-fill. Consumers read the cached
        // EffectiveThresholds off HttpContext.Items in follow-up work.
        services.TryAddSingleton<SiteProfiles.IEffectivePolicyResolver, SiteProfiles.EffectivePolicyResolver>();
        // Operator-declared per-(host, method, path, transport, protocol) policies.
        // Runs before bot detection; matched rules dispatch a named action via the
        // existing IActionPolicyRegistry. Pre-detection layer -- no detection cost
        // for hard-blocked requests.
        services.AddOptions<EndpointPolicies.EndpointPolicyOptions>()
            .BindConfiguration(EndpointPolicies.EndpointPolicyOptions.SectionName);
        services.TryAddSingleton<EndpointPolicies.IEndpointPolicyResolver, EndpointPolicies.ConfigEndpointPolicyResolver>();
        // Behavioural grouper -- single bridge consulted by every dashboard
        // surface that wants to collapse rows. Enforces a non-overridable
        // bot-only gate (humans never group) followed by a 7-tier hierarchy
        // from Identity down to raw signature.
        services.AddOptions<Grouping.GroupingOptions>()
            .BindConfiguration(Grouping.GroupingOptions.SectionName);
        services.TryAddSingleton<Grouping.ISubnetRotationTracker, Grouping.SubnetRotationTracker>();
        services.TryAddSingleton<Grouping.ISessionSimilarityLookup, Grouping.BoundedSessionSimilarityLookup>();
        services.TryAddSingleton<Grouping.IBehaviouralGrouper, Grouping.BehaviouralGrouper>();
        // Honeypot response policy -- jittered rate-limit + fake response.
        // Registered as IActionPolicy under name "honeypot-response"; the middleware
        // auto-selects it when the tagger set a tier tag on HttpContext.Items.
        services.TryAddSingleton<Honeypot.HoneypotRateLimiter>();
        services.AddSingleton<Honeypot.HoneypotResponseActionPolicy>();
        services.AddSingleton<IActionPolicy>(sp => sp.GetRequiredService<Honeypot.HoneypotResponseActionPolicy>());
        // Path lifecycle store -- records per-path response history so the honeypot
        // threat scorer can lift the score when a 4xx hits a path that used to serve
        // real content (scanner has institutional memory of a removed endpoint).
        // PathLifecycle uses the canonical WriteBehindLfuStore pattern: hot
        // ConcurrentDictionary + bounded channel + single background drainer.
        // No bespoke flush service needed -- the base class owns persistence
        // cadence. Tunable via BotDetection:PathLifecycle:* (LFU cap, channel
        // size, batch size, drain interval).
        services.TryAddSingleton<Lifecycle.IPathLifecycleStore>(sp =>
        {
            var env = sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>();
            var dbPath = env != null
                ? Path.Combine(env.ContentRootPath, "path-lifecycle.db")
                : "path-lifecycle.db";
            var logger = sp.GetRequiredService<ILogger<Lifecycle.SqlitePathLifecycleStore>>();
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Models.BotDetectionOptions>>()
                .Value.PathLifecycle;
            return new Lifecycle.SqlitePathLifecycleStore(
                $"Data Source={dbPath};Cache=Shared", logger, opts);
        });
        services.AddSingleton<IContributingDetector, Honeypot.EndpointHistoryContributor>();
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
        // Identity-layer fingerprint-pool collision tracker (Bonus A).
        // Subclass of WriteBehindLfuStore<TKey, TValue, TWriteOp>: hot
        // ConcurrentDictionary tier + SQLite-backed durable tier in a
        // separate pool_collisions.db file. The init service creates the
        // schema at startup; the store itself is the singleton everyone
        // injects via IFingerprintPoolCollisionTracker.
        services.TryAddSingleton<SqlitePoolCollisionStore>();
        services.TryAddSingleton<IFingerprintPoolCollisionTracker>(sp => sp.GetRequiredService<SqlitePoolCollisionStore>());
        services.AddHostedService<Mostlylucid.BotDetection.Storage.StoreInitService<SqlitePoolCollisionStore>>();

        // Sticky-deny store: same WriteBehindLfuStore pattern. SQLite tier
        // makes the block window durable across restarts so a bot can't
        // escape its block by waiting for the process to recycle.
        //
        // StickyDenyActionOptions lives on BotDetectionOptions.StickyDeny so
        // appsettings.json under "BotDetection:StickyDeny" tunes it just like
        // any other nested section. The earlier `TryAddSingleton<T>()`
        // registration bypassed the IOptions binding entirely -- operators
        // couldn't change ViolationThreshold / WindowSeconds / BlockTtlSeconds
        // without recompiling, despite the comment above claiming otherwise.
        services.TryAddSingleton(sp =>
            sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value.StickyDeny);
        services.TryAddSingleton<Mostlylucid.BotDetection.Actions.SqliteStickyDenyStore>();
        services.TryAddSingleton<Mostlylucid.BotDetection.Actions.IStickyDenyTracker>(
            sp => sp.GetRequiredService<Mostlylucid.BotDetection.Actions.SqliteStickyDenyStore>());
        services.AddHostedService<Mostlylucid.BotDetection.Storage.StoreInitService<Mostlylucid.BotDetection.Actions.SqliteStickyDenyStore>>();
        services.AddSingleton<IContributingDetector, PoolCollisionContributor>();
        // Advanced fingerprinting detectors (Wave 0 - network/protocol layer)
        services.TryAddSingleton<Ja3ReferenceIndex>();
        services.TryAddSingleton<IJa3ReferenceIndex>(sp => sp.GetRequiredService<Ja3ReferenceIndex>());
        services.TryAddSingleton<Ja3CorpusEnvelopeVerifier>();
        // TLS corpus refresh service: opt-in. Registers an HttpClient via the
        // factory and a BackgroundService that periodically refreshes the
        // in-memory Ja3ReferenceIndex from a signed envelope. The service
        // self-aborts at runtime if URL or public key are empty, so this
        // registration is safe to include unconditionally; the operator
        // controls activation via TlsCorpus:Enabled.
        services.AddHttpClient(Ja3CorpusRefreshService.HttpClientName);
        services.TryAddSingleton<Ja3CorpusRefreshService>();

        // Arcjet well-known-bots catalog: ~635 bot UA patterns downloaded periodically.
        // Returns WellKnownBotIndex.Default so DI consumers and static callers
        // (FingerprintNameComposer, BotPatternLoader) share the same instance.
        services.TryAddSingleton(Definitions.WellKnownBots.WellKnownBotIndex.Default);
        services.AddHttpClient(Definitions.WellKnownBots.WellKnownBotRefreshService.HttpClientName);
        services.TryAddSingleton<Definitions.WellKnownBots.WellKnownBotRefreshService>();
        // Guardian framework (FOSS-core). Walks registered IGuardians on their own
        // intervals; harmless no-op until data/compliance/license guardians register.
        services.TryAddSingleton<Guardians.GuardianService>();
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
        // Behavioral waveform analysis - analyzes patterns across multiple requests.
        // WaveformHistoryStore replaces the previous IMemoryCache pattern with the
        // canonical WriteBehindLfuStore subclass (hot ConcurrentDictionary tier +
        // SQLite-backed durable tier in waveform_history.db).
        services.TryAddSingleton<WaveformHistoryStore>();
        services.AddHostedService<Mostlylucid.BotDetection.Storage.StoreInitService<WaveformHistoryStore>>();
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
        // Wave 2 migrated: DeploymentNormCalibrationService is now a plain
        // singleton that subscribes to ScheduleCoordinator.Tick1s at
        // construction. The BotDetectionHostedSingletonsBootstrap shim (below)
        // eagerly resolves it at boot so the subscription is live before any
        // requests land.
        services.AddSingleton<Services.DeploymentNormCalibrationService>();
        services.AddHostedService<Scheduling.BotDetectionHostedSingletonsBootstrap>();
        // Wave 2 (Category B): SessionPersistenceService dropped BackgroundService
        // inheritance; subscribes to ScheduleCoordinator Tick10s + the
        // SessionStore.SessionFinalized event at construction. The lifecycle
        // shim handles boot-time ISessionStore init + graceful-shutdown drain
        // (these are not part of any tick).
        services.AddSingleton<Data.SessionPersistenceService>();
        services.AddHostedService<Data.SessionPersistenceLifecycleHostedService>();
        // Per-request persistence (every request → SQLite, LFU sampled under load)
        services.AddSingleton<Data.RequestPersistenceService>();
        // Pipeline load sensor — adaptive multi-signal pressure detection; used
        // by background services to self-throttle and by LoadShedDecision to
        // skip detection / refuse-503 under sustained pressure.
        services.TryAddSingleton<Services.PipelineLoadSensor>(sp =>
        {
            var o = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Models.BotDetectionOptions>>()
                .Value.PipelineLoadSensor;
            return new Services.PipelineLoadSensor(
                normalRps: o.NormalRps,
                highRps: o.HighRps,
                criticalRps: o.CriticalRps,
                highRatio: o.HighRatio,
                criticalRatio: o.CriticalRatio,
                highStarvedTicks: o.HighStarvedTicks,
                criticalStarvedTicks: o.CriticalStarvedTicks,
                highGen2PerSec: o.HighGen2PerSec,
                criticalGen2PerSec: o.CriticalGen2PerSec,
                baselineWindowSamples: o.BaselineWindowSamples,
                baselinePercentile: o.BaselinePercentile,
                baselineUpwardDriftPerTick: o.BaselineUpwardDriftPerTick);
        });
        services.AddSingleton<Services.ILoadBandSource>(sp => sp.GetRequiredService<Services.PipelineLoadSensor>());
        services.AddSingleton<Services.LoadShedDecision>();
        // Per-endpoint perf baseline for the load-shed hot path.
        // TryAdd NullEndpointPerfBaseline as the default so hosts without
        // an IDashboardEventStore boot; deployments that DO register the
        // store can call AddDashboardEndpointPerfBaseline() to replace it
        // with the DashboardEventStore-backed impl. Per the
        // remote-mode-optional-DI rule, the middleware tolerates either.
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .TryAddSingleton<Services.IEndpointPerfBaseline, Services.NullEndpointPerfBaseline>(services);
        // Pumps the sensor's live state into the per-request signal vocabulary
        // (pressure.band, pressure.detection_latency_ratio, ...) so policy rule
        // predicates can react to system pressure without a metrics round-trip.
        services.AddSingleton<Policies.Signals.ISignalContributor, Services.PressureSignalContributor>();
        // Session atomization from raw requests.
        // Wave 2: subscribes to IScheduleCoordinator.Tick1m at construction
        // (gated on RetentionOptions.AtomizerRunInterval); drop
        // AddHostedService and add to the BotDetectionHostedSingletonsBootstrap
        // eager-resolve chain.
        services.AddSingleton<Services.SessionAtomizerService>();
        // Entity resolution - merge/split/rewind analysis.
        // Wave 2: subscribes to IScheduleCoordinator.Tick1m at construction
        // (load-sensor aware), so drop AddHostedService and add to the
        // BotDetectionHostedSingletonsBootstrap eager-resolve chain.
        services.AddSingleton<Services.EntityResolutionService>();
        // Markov chain path learning and drift detection
        services.TryAddSingleton<Markov.MarkovTracker>();
        services.TryAddSingleton<Clustering.AdaptiveSimilarityWeighter>();
        // Wave 2: migrated to ScheduleCoordinator tick.10s. Eager-resolved by
        // BotDetectionHostedSingletonsBootstrap so the constructor's Subscribe(...)
        // fires at boot.
        services.AddSingleton<Markov.PopulationMarkovService>();

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
        // Default IClusterStore binding forwards to the Sqlite concrete; commercial
        // plugin Replace()s this with PostgreSQLClusterStore at gateway startup.
        services.TryAddSingleton<IClusterStore>(sp => sp.GetRequiredService<SqliteClusterStore>());
        // KNN graph builder: feeds the Leiden cluster service a sparse K-NN
        // graph (O(N log N) via sqlite-vec when the extension is loadable,
        // O(N^2) brute-force fallback). Replaces the historic inline N^2
        // BuildSimilarityGraph at any meaningful scale. See
        // docs/research/2026-05-24-grouping-systems-audit.md.
        services.TryAddSingleton<Clustering.Knn.BruteForceKnnGraphBuilder>();
        services.TryAddSingleton<Clustering.Knn.SqliteVecKnnGraphBuilder>(sp =>
            new Clustering.Knn.SqliteVecKnnGraphBuilder(
                sp.GetRequiredService<ILogger<Clustering.Knn.SqliteVecKnnGraphBuilder>>()));
        services.TryAddSingleton<Clustering.Knn.IKnnGraphBuilder>(sp =>
            new Clustering.Knn.KnnGraphBuilderResolver(
                sp.GetRequiredService<Clustering.Knn.SqliteVecKnnGraphBuilder>(),
                sp.GetRequiredService<Clustering.Knn.BruteForceKnnGraphBuilder>(),
                sp.GetRequiredService<ILogger<Clustering.Knn.KnnGraphBuilderResolver>>()));

        services.TryAddSingleton<BotClusterService>();
        services.AddHostedService(sp => sp.GetRequiredService<BotClusterService>());
        // Expose the read-only slice so the dashboard / REST endpoints resolve via interface
        // (remote-mode hosts substitute a HTTP-backed impl).
        services.TryAddSingleton<IBotClusterReader>(sp => sp.GetRequiredService<BotClusterService>());
        // Fast reverse lookup (signature -> cluster) consumed by the unified
        // SignatureRiskVerdictComposer so threat-band / risk-profile derivation
        // doesn't have to walk all clusters per signature.
        services.TryAddSingleton<IClusterMembershipLookup>(sp => sp.GetRequiredService<BotClusterService>());
        // Signature convergence - merges/splits related signatures (same IP, rotating UAs)
        // Wave 2: migrated to ScheduleCoordinator tick.10s. Eager-resolved by
        // BotDetectionHostedSingletonsBootstrap so the constructor's Subscribe(...)
        // fires at boot.
        services.TryAddSingleton<SignatureConvergenceService>();
        services.AddSingleton<IContributingDetector, ClusterContributor>();

        // Content sequence detection — Priority 4, runs before all other detectors
        services.TryAddSingleton<SequenceContextStore>();
        services.AddSingleton(sp =>
        {
            var connStr = CentroidConnStr(sp);
            var logger = sp.GetRequiredService<ILogger<CentroidSequenceStore>>();
            var sessionStore = sp.GetService<ISessionStore>();

            // Bind the loader to the ISessionStore interface, not the SqliteSessionStore
            // concrete. The methods used here (GetRecentSessionsAsync, GetSessionsAsync)
            // are both on the interface; the prior cast meant ephemeral mode -- where
            // ISessionStore resolves to NullSessionStore -- got loader=null and the
            // content-sequence contributor silently suppressed divergence scoring for
            // every fingerprint that fell back to the global chain. Now the lambda runs,
            // NullSessionStore returns empty lists, RelearnGlobalAsync sees zero
            // baseline data and the contributor still gracefully no-ops -- without the
            // "concrete type required" trap.
            CentroidSequenceStore.ClusterSessionLoader? loader = null;
            if (sessionStore is not null)
            {
                loader = async (signatures, perSig, ct) =>
                {
                    var result = new List<SessionTransitionData>();
                    if (signatures.Count == 0)
                    {
                        // Broad sample for learned-global baseline: recent confirmed-human sessions.
                        var recent = await sessionStore.GetRecentSessionsAsync(limit: perSig, isBot: false, ct: ct);
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
                        var sessions = await sessionStore.GetSessionsAsync(sig, perSig, ct);
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

            return new CentroidSequenceStore(() => new SqliteConnection(connStr), logger, loader);
        });
        services.TryAddSingleton<EndpointDivergenceTracker>();
        services.AddSingleton(sp =>
        {
            var connStr = CentroidConnStr(sp);
            var centroidStore = sp.GetRequiredService<CentroidSequenceStore>();
            var logger = sp.GetRequiredService<ILogger<AssetHashStore>>();
            return new AssetHashStore(() => new SqliteConnection(connStr), centroidStore, logger);
        });
        services.AddSingleton<IContributingDetector, ContentSequenceContributor>();
        services.AddHostedService<CentroidSequenceRebuildHostedService>();
        services.AddHostedService<AssetHashInitHostedService>();
        // ==========================================
        // Ephemeral LLM Namer pipelines (EC6c)
        // ==========================================
        // Per-(TItem,TResult) EphemeralLlmCoordinator drives picker -> prompter ->
        // invoker -> writeback against a ScheduleCoordinator tick instead of the
        // legacy LlmDescriptionCoordinator queue + BackgroundService pair. The
        // picker is registered as the concrete type AND as IEphemeralPicker<T>
        // pointing at the same singleton so middleware callers needing the
        // concrete TrackClusters entry point and the coordinator resolving
        // IEphemeralPicker<T> share state. Fingerprint-naming picker is purely
        // atom-driven -- it walks IFingerprintStore.EnumerateLlmRepickCandidates
        // on tick and needs no middleware push (LL1, replaces the per-signature
        // picker + DetectionBroadcastMiddleware.TrackSignature push).
        services.AddFingerprintLlmNamer();
        services.AddClusterLlmNamer();

        // BotClusterDescriptionService still owns the IClusterDescriptionCallback
        // broadcast on ClustersUpdated (non-LLM, immediate). Its enqueue path is
        // re-pointed onto NeedsDescriptionClusterPicker.TrackClusters; the legacy
        // LlmDescriptionCoordinator queue is gone.
        services.AddSingleton<BotClusterDescriptionService>();

        // ==========================================
        // Bot Name Synthesizer (provided by LLM plugin packages)
        // ==========================================
        // Default no-op synthesizer - replaced by Mostlylucid.BotDetection.Llm.* packages
        // Deterministic naming from signals (immediate, no LLM required).
        // LLM packages override this with richer AI-generated names when available.
        services.TryAddSingleton<IBotNameSynthesizer, DeterministicBotNameSynthesizer>();

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
        // Wave 2: dropped BackgroundService inheritance; subscribes to
        // ScheduleCoordinator Tick10s at construction. BotDetectionHosted-
        // SingletonsBootstrap eagerly resolves it at boot so the subscription
        // is live before the first tick.
        services.AddSingleton<BackgroundEnrichmentService>();

        // ==========================================
        // Background LLM Classification Coordinator
        // ==========================================
        // Wave 2 (Category B): dropped BackgroundService inheritance; subscribes
        // to ScheduleCoordinator Tick10s at construction. BotDetectionHosted-
        // SingletonsBootstrap eagerly resolves it at boot so the subscription
        // is live before the first tick.
        services.AddSingleton<LlmClassificationCoordinator>();

        // ==========================================
        // Background Intent Classification Coordinator (threat scoring)
        // ==========================================
        // Wave 2 (Category B): mirror of LlmClassificationCoordinator -- now a
        // singleton that subscribes to ScheduleCoordinator Tick10s; eagerly
        // resolved by BotDetectionHostedSingletonsBootstrap.
        services.AddSingleton<IntentClassificationCoordinator>();

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
        // Nightly compaction: SQLite session compaction + centroid pruning.
        // Wave 2: subscribes to IScheduleCoordinator.Tick1h at construction and
        // gates the work on "current UTC hour matches CompactionHourUtc AND
        // we haven't run today". Drop AddHostedService and add to the
        // BotDetectionHostedSingletonsBootstrap eager-resolve chain.
        services.AddSingleton<Services.VectorCompactionService>();
        // Storage compaction is now a data-category guardian: the GuardianService
        // walker drives it on RetentionOptions.CompactionInterval instead of the
        // old daily hour-gate. Forwarded so it joins the guardian roster.
        services.AddSingleton<Guardians.IGuardian>(sp => sp.GetRequiredService<Services.VectorCompactionService>());

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

        // Policy-stack dispatcher: bridges the new PolicyAction record family
        // (Allow / Block / Observe / Tag / Challenge / RateLimit / Throttle)
        // to the HTTP request pipeline. Optional from the middleware's POV
        // (BotDetectionMiddleware resolves it nullable from RequestServices),
        // so registering it here costs nothing at runtime for hosts that don't
        // exercise the policy stack. The handlers + Phase G primitives are
        // all TryAdd so commercial packs can override individual pieces.
        services.AddPolicyDispatcher();

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

        // Rate-limit token-bucket store (phase 2 of the policy-grammar work).
        // Default to the in-memory implementation; consumers can override
        // before AddBotDetection() runs to swap in a SQLite-backed store.
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimit.ITokenBucketStore,
            Mostlylucid.BotDetection.RateLimit.InMemoryTokenBucketStore>();

        // Adaptive scaling primitives (phase 4): DegradationAtom tracks
        // upstream health, HysteresisTracker damps tier transitions, and
        // AdaptiveScalingTracker combines them with the configured tier
        // ladder to produce the active multiplier.
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimit.DegradationAtom>();
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimit.HysteresisTracker>();
        services.Configure<Mostlylucid.BotDetection.RateLimit.AdaptiveScalingOptions>(_ => { });
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimit.IAdaptiveScalingTracker,
            Mostlylucid.BotDetection.RateLimit.AdaptiveScalingTracker>();

        // Upstream-health gate: when 4xx/5xx EMAs cross threshold (after a
        // min sample count), status-derived bot signals (response-status
        // boost, 404 scan patterns, reputation lane error indicators,
        // heuristic 404 weights, Markov NotFound transitions) stand down so
        // cold-start / origin-down windows don't falsely flag legitimate
        // visitors and poison persisted centroid samples with outage shape.
        services.AddOptions<Mostlylucid.BotDetection.RateLimit.UpstreamHealthOptions>()
            .BindConfiguration("BotDetection:UpstreamHealth");
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimit.UpstreamHealthGate>();

        // Gateway-warmup gate: behavioural sibling of UpstreamHealthGate.
        // Upstream-health protects status-derived signals when the protected
        // site is cold-starting or down; this gate protects BEHAVIOURAL
        // signals (session-vector contributions, Markov downstream consumers,
        // sigv:* heuristic features, per-signature drift) when stylobot
        // itself just booted and behavioural classifiers haven't accumulated
        // enough samples to score reliably. Rules still fire; identity / UA /
        // header / honeypot detection still runs. Stamps gateway.warmup on
        // every detection event so persisted centroids segment cold-start
        // shape out of the natural prior.
        services.AddOptions<Mostlylucid.BotDetection.Lifecycle.GatewayWarmupOptions>()
            .BindConfiguration("BotDetection:GatewayWarmup");
        services.TryAddSingleton<Mostlylucid.BotDetection.Lifecycle.GatewayWarmupGate>();

        // Site-health history: DegradationStoreSampler subscribes to Tick10s
        // and persists DegradationAtom snapshots via IDashboardEventStore
        // (SQLite default, Postgres on commercial). The earlier
        // in-memory DegradationHistoryAtom ring lost the whole window on
        // restart and violated [[feedback_no_inmemory_stores]]; the
        // sampler is now registered in AddStyloBotDashboard so it lives
        // next to its storage dependency.

        // Register action policy registry (holds named action policies)
        services.TryAddSingleton<IActionPolicyRegistry, ActionPolicyRegistry>();
        // Read model for the dashboard policy tab (phase 1 of the policy-
        // grammar work; see docs/plans/2026-05-24-policy-grammar-core-experience.md).
        services.TryAddSingleton<IPolicyStateProvider, RegistryPolicyStateProvider>();

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

        // Meter-signals extension point (IMeterSignalSink / NullMeterSignalSink)
        // lives in Mostlylucid.BotDetection.PrometheusPack now -- AddLocalMeterStream
        // / AddRemoteMeterStream register the default null sink there.

        return services;
    }

    /// <summary>
    ///     Registers the per-FINGERPRINT LLM-naming pipeline (LL1, spec §3.2 +
    ///     §7): drift-triggered picker, in-flight reservation set, prompter /
    ///     invoker / writeback, and the per-(TItem,TResult)
    ///     EphemeralLlmCoordinator with bootstrap. Replaces the legacy per-
    ///     SIGNATURE pipeline -- the picker now walks
    ///     <see cref="IFingerprintStore.EnumerateLlmRepickCandidates"/> directly
    ///     (atom-only, no DB) instead of a push-tracked signature LFU, and the
    ///     writeback persists into <c>Fingerprint.LlmName</c> via
    ///     <see cref="IFingerprintStore.UpdateLlmNameAsync"/> instead of the
    ///     SignalR signature-name callback. The concrete
    ///     <see cref="DriftTriggeredFingerprintPicker"/> and the
    ///     <see cref="IEphemeralPicker{T}"/> facet resolve to the SAME singleton
    ///     so the in-flight reservation set is observed consistently across
    ///     callers (no parallel-axis pickers).
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
    ///     Registers the cluster LLM-naming pipeline: needs-description picker,
    ///     in-flight reservation set, prompter / invoker / writeback, and the
    ///     per-(TItem,TResult) EphemeralLlmCoordinator with bootstrap. The
    ///     concrete <see cref="NeedsDescriptionClusterPicker"/> singleton and the
    ///     <see cref="IEphemeralPicker{T}"/> facet resolve to the SAME instance so
    ///     callers that push via <c>TrackClusters(...)</c> and the coordinator
    ///     pulling via <c>Pick(...)</c> share state.
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
