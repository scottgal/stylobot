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
        // BotListDatabase is the FOSS default; also serve it as IBotListDatabase
        // so atoms + services that DI the interface pick it up.
        services.TryAddSingleton<Data.IBotListDatabase>(sp => sp.GetRequiredService<BotListDatabase>());
        // ScheduleCoordinator drives every migrated tick-driven service
        // (BotListUpdateService, BackgroundEnrichmentService, EntityResolution,
        // absorption, etc.). Also a hosted service so the cadence loops start
        // at boot. Watchdog logs when a subscriber's tick handler runs long.
        services.AddOptions<Scheduling.ScheduleCoordinatorOptions>()
            .BindConfiguration(Scheduling.ScheduleCoordinatorOptions.SectionName);
        services.TryAddSingleton<Scheduling.ScheduleCoordinator>();
        services.TryAddSingleton<Mostlylucid.Common.Scheduling.IScheduleCoordinator>(
            sp => sp.GetRequiredService<Scheduling.ScheduleCoordinator>());
        services.AddHostedService(sp => sp.GetRequiredService<Scheduling.ScheduleCoordinator>());
        services.AddHostedService<Scheduling.ScheduleCoordinatorWatchdog>();
        // Boot the migrated singletons (each subscribes to ticks at ctor time).
        services.AddHostedService<Scheduling.BotDetectionHostedSingletonsBootstrap>();

        // Manifest loader + config provider back every detector atom's
        // per-manifest weights / thresholds / feature flags. FOSS ships the
        // YAML manifests as embedded resources under Orchestration/Manifests/.
        services.TryAddSingleton<Orchestration.Manifests.DetectorManifestLoader>();
        services.TryAddSingleton<Orchestration.Manifests.IDetectorConfigProvider,
            Orchestration.Manifests.DetectorConfigProvider>();

        // Detection event publisher — Null default so DetectionBroadcastMiddleware
        // (mounted unconditionally by the dashboard middleware chain) can resolve
        // even when observability isn't configured. AddStyloBotObservability
        // conditionally RemoveAll + AddSingleton the Serilog publisher on top;
        // that swap is untouched. Without the Null default the first request
        // would crash before the pipeline could return a verdict.
        services.TryAddSingleton<Orchestration.Telemetry.IDetectionEventPublisher,
            Orchestration.Telemetry.NullDetectionEventPublisher>();

        // IBotListFetcher backs BotListDatabase's data-source pulls (bot patterns,
        // datacenter IP ranges, cloud vendor ranges) and is also a required dep
        // for SecurityToolAtom, InMemoryBotListDatabase, ListUpdateCoordinatorAtom,
        // and the CloudRangesProvider threat-intel provider. Uses IHttpClientFactory
        // + IMemoryCache — both required-dependencies come from AddHttpClient +
        // AddMemoryCache which are registered by the host bootstrap.
        services.AddHttpClient();
        services.AddMemoryCache();
        services.TryAddSingleton<Data.IBotListFetcher, Data.BotListFetcher>();

        // Store defaults — FOSS ships null/in-memory/config-driven variants.
        // Commercial persistence packs replace via TryAdd-loses at their own
        // Add* call site (SQLite pack, Postgres pack).
        services.TryAddSingleton<Actions.IChallengeStore, Actions.InMemoryChallengeStore>();
        services.TryAddSingleton<Data.IFingerprintApprovalStore, Data.NullFingerprintApprovalStore>();
        services.TryAddSingleton<Data.IPatternReputationCache, Data.InMemoryPatternReputationCache>();
        services.TryAddSingleton<Honeypot.IHoneypotExemptStore, Honeypot.ConfigHoneypotExemptStore>();
        services.TryAddSingleton<Lifecycle.IPathLifecycleStore, Lifecycle.NullPathLifecycleStore>();
        // Fingerprint store — FOSS default is SQLite (SqliteFingerprintStore),
        // which the BrowserModes subsystem also depends on directly. Commercial
        // pack replaces with a shared-schema Postgres variant via TryAdd-loses.
        services.TryAddSingleton<Identity.SqliteFingerprintStore>();
        services.TryAddSingleton<Identity.IFingerprintStore>(
            sp => sp.GetRequiredService<Identity.SqliteFingerprintStore>());
        services.TryAddSingleton<Identity.BrowserModes.IFingerprintBrowserModeStore,
            Identity.BrowserModes.SqliteFingerprintBrowserModeStore>();
        services.TryAddSingleton<ThreatIntel.ICveFingerprintMatcher, ThreatIntel.NullCveFingerprintMatcher>();
        // Pool-collision tracker only has a SQLite impl; FOSS default wires the
        // in-process SQLite path (fresh DB per-process when no connection string
        // is supplied). Commercial swaps this via TryAdd-loses on the Postgres pack.
        services.TryAddSingleton<Identity.SqlitePoolCollisionStore>();
        services.TryAddSingleton<Identity.IFingerprintPoolCollisionTracker>(
            sp => sp.GetRequiredService<Identity.SqlitePoolCollisionStore>());
        // Browser-mode resolver: centroid classifier is the FOSS default.
        // CachingBrowserModeResolver wraps a BrowserModeRegistry (not this
        // resolver); it's registered separately by hosts that want the
        // pre-materialised mode list. TryAdd so commercial can replace.
        services.TryAddSingleton<Identity.BrowserModes.CentroidBrowserModeResolver>();
        services.TryAddSingleton<Identity.BrowserModes.IBrowserModeResolver>(
            sp => sp.GetRequiredService<Identity.BrowserModes.CentroidBrowserModeResolver>());

        // HttpContextAccessor drives ~30 atoms that read Request.Path/Headers
        // during DetectAsync. Idempotent — safe to add here even if the host
        // already registered it.
        services.AddHttpContextAccessor();

        // Identity subsystem primitives.
        services.TryAddSingleton<Identity.IdentityVectorLayout>(_ => Identity.IdentityVectorLayout.DefaultV1());
        services.TryAddSingleton<Identity.IdentityVectorEncoder>();
        services.TryAddSingleton<Identity.EncoderResultCache>();
        services.TryAddSingleton<Identity.HeaderHashCollector>();
        // Anchor index: brute-force is the FOSS default (no sqlite-vec dep);
        // commercial swaps to SqliteVec via TryAdd-loses in the Postgres pack.
        services.TryAddSingleton<Identity.IIdentityAnchorIndex, Identity.BruteForceIdentityAnchorIndex>();
        // Browser-mode seed source: YAML-loaded catalogue is the FOSS default;
        // HardcodedBrowserModeSeedSource fallback is for tests.
        services.TryAddSingleton<Identity.BrowserModes.IBrowserModeSeedSource,
            Identity.BrowserModes.YamlBrowserModeSeedSource>();
        services.TryAddSingleton<Identity.BrowserModes.ModeCentroidCatalogue>();
        // ModeCentroidClassifier's ctor is private (LoadAsync static factory);
        // materialize once at boot. Same blocking-await pattern as SignalCatalog
        // in AddStyloBotDashboard.
        services.TryAddSingleton<Identity.BrowserModes.ModeCentroidClassifier>(sp =>
        {
            var cat = sp.GetRequiredService<Identity.BrowserModes.ModeCentroidCatalogue>();
            var layout = sp.GetRequiredService<Identity.IdentityVectorLayout>();
            return Identity.BrowserModes.ModeCentroidClassifier
                .LoadAsync(cat, layout).GetAwaiter().GetResult();
        });
        // Signature scoring / dashboard aggregate service (also read by
        // SignatureAtom for verdict composition).
        services.TryAddSingleton<Dashboard.MultiFactorSignatureService>();
        // Waveform history is a per-fingerprint ring buffer feeding
        // BehavioralWaveformAtom's spectral analysis.
        services.TryAddSingleton<Orchestration.Atoms.WaveformHistoryStore>();
        // Detectors (legacy) still injected by 4 atoms. FOSS defaults.
        services.TryAddSingleton<Detectors.HeuristicDetector>();
        services.TryAddSingleton<Detectors.VersionAgeDetector>();
        services.TryAddSingleton<Detectors.BehavioralDetector>();
        services.TryAddSingleton<Detectors.ClientSideDetector>();
        // Similarity primitives.
        services.TryAddSingleton<Similarity.FeatureVectorizer>();
        services.TryAddSingleton<Similarity.IntentVectorizer>();
        // Support services.
        services.TryAddSingleton<Services.VerifiedBotRegistry>();
        services.TryAddSingleton<Services.ProjectHoneypotLookupService>();
        services.TryAddSingleton<Services.IFediverseDomainVerifier, Services.FediverseDomainVerifier>();
        services.TryAddSingleton<Services.IBrowserVersionService>(sp => sp.GetRequiredService<BrowserVersionService>());
        services.TryAddSingleton<Services.IDnsResolver, Services.SystemDnsResolver>();
        services.TryAddSingleton<Services.UaProfileStore>();
        services.TryAddSingleton<Services.CountryReputationTracker>();
        services.TryAddSingleton<Services.ReactiveSignalTracker>();
        services.TryAddSingleton<Services.BotClusterService>();
        services.AddHostedService(sp => sp.GetRequiredService<Services.BotClusterService>());
        services.TryAddSingleton<Analysis.DeploymentNormTracker>();
        // Legacy Analysis.SessionStore (per-session sliding window of vectors)
        // still used by SessionVectorAtom. Distinct from the new
        // Orchestration.Sessions.SessionStore (shared per-domain sink).
        services.TryAddSingleton<Analysis.SessionStore>();
        // Client-side fingerprint population tracker (browser fingerprint
        // observed-value distribution).
        services.TryAddSingleton<ClientSide.FingerprintPopulationTracker>();
        // Centroid stores — FOSS defaults use in-memory / SQLite paths.
        // NullSignatureCentroidStore keeps a fresh install boot-fast; hosts
        // that want durability wire SqliteSignatureCentroidStore explicitly
        // via TryAdd-loses replacement.
        services.TryAddSingleton<Data.Contracts.ISignatureCentroidStore, Data.NullSignatureCentroidStore>();
        services.TryAddSingleton<Data.Contracts.IIntentCentroidStore, Data.NullIntentCentroidStore>();
        // Identity archetype registry (loaded from embedded YAML archetypes).
        services.TryAddSingleton<Identity.IdentityArchetypeRegistry>();
        // SequenceContextStore — per-fingerprint sliding sequence of request
        // signals feeding ContentSequenceAtom.
        services.TryAddSingleton<Services.SequenceContextStore>();
        // Centroid sequence store — per-fingerprint sequence-of-centroids
        // history fed to ContentSequenceAtom.
        services.TryAddSingleton<Services.CentroidSequenceStore>();
        // Endpoint divergence tracker — per-fingerprint deviation-from-expected
        // endpoint access pattern; feeds ContentSequenceAtom.
        services.TryAddSingleton<Services.EndpointDivergenceTracker>();
        // Identity global weights cache (per-slot weight decay + updates).
        services.TryAddSingleton<Identity.IdentityGlobalWeightsCache>();
        // IdentityProcessingCoordinator — Pass-2 coalescer for FingerprintMatchAtom.
        services.TryAddSingleton<Identity.IdentityProcessingCoordinator>();
        // Func<DbConnection> — FOSS default SQLite connection factory.
        // BotDetectionOptions.DatabasePath drives the file path; empty means
        // a shared in-memory DB (fresh per process). Commercial Postgres pack
        // overrides via TryAdd-loses at its Add* site.
        services.TryAddSingleton<Func<System.Data.Common.DbConnection>>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>().Value;
            var connString = string.IsNullOrEmpty(opts.DatabasePath)
                ? "Data Source=file::memory:?cache=shared;Mode=Memory;Cache=Shared"
                : $"Data Source={opts.DatabasePath};Cache=Shared";
            return () => new Microsoft.Data.Sqlite.SqliteConnection(connString);
        });
        // SignatureCoordinator — per-fingerprint verdict aggregator that
        // multiple atoms read. Has an options block.
        services.AddOptions<Orchestration.SignatureCoordinatorOptions>()
            .BindConfiguration("BotDetection:SignatureCoordinator");
        services.TryAddSingleton<Orchestration.SignatureCoordinator>();
        // ClientSide fingerprint store (browser-side collected metrics).
        services.TryAddSingleton<ClientSide.IBrowserFingerprintStore, ClientSide.BrowserFingerprintStore>();
        // ClientSide analyzer + token service + metrics, consumed by BrowserFingerprintEndpoint.
        // These were registered alongside the deleted ClientSideContributor (Step-7 contributor
        // delete) and dropped with it — leaving the minimal-API endpoint with un-inferrable
        // parameters, which fails host startup ("Failure to infer one or more parameters").
        services.TryAddSingleton<ClientSide.IBrowserFingerprintAnalyzer, ClientSide.BrowserFingerprintAnalyzer>();
        services.TryAddSingleton<ClientSide.IBrowserTokenService, ClientSide.BrowserTokenService>();
        services.TryAddSingleton<Metrics.BotDetectionMetrics>();
        // Pattern-reputation updater feeds the reputation cache from detection outcomes.
        services.TryAddSingleton<Data.PatternReputationUpdater>();
        // Fingerprint dimension snapshot cache (waveform + similarity input).
        services.TryAddSingleton<Orchestration.Atoms.FingerprintDimSnapshotCache>();
        // Similarity search — FOSS defaults use in-memory sliding-window impls;
        // commercial pgvector pack swaps via TryAdd-loses.
        services.TryAddSingleton<Similarity.IIntentSimilaritySearch, Similarity.SlimIntentSearch>();
        services.TryAddSingleton<Similarity.ISignatureSimilaritySearch, Similarity.SlimSignatureSimilaritySearch>();
        // Simulation pack registry (BDF replay + adversarial sim harness).
        services.TryAddSingleton<SimulationPacks.ISimulationPackRegistry,
            SimulationPacks.SimulationPackLoader>();
        // PII hasher — FOSS default seeds with a fixed key so DI resolves;
        // operators override via their own AddSingleton<PiiHasher>(sp => ...).
        // Commercial (licensing pack) replaces with a JWT-derived key.
        services.TryAddSingleton<Privacy.PiiHasher>(_ =>
            new Privacy.PiiHasher(
                System.Text.Encoding.UTF8.GetBytes("stylobot-foss-default-key-rotate-per-deployment")));
        // IBotListFetcher backs BotListDatabase's data-source pulls (bot patterns,
        // datacenter IP ranges, cloud vendor ranges) and is also a required dep
        // for SecurityToolAtom, InMemoryBotListDatabase, ListUpdateCoordinatorAtom,
        // and the CloudRangesProvider threat-intel provider. Uses IHttpClientFactory
        // + IMemoryCache — both required-dependencies come from AddHttpClient +
        // AddMemoryCache which are registered by the host bootstrap.
        services.AddHttpClient();
        services.AddMemoryCache();
        services.TryAddSingleton<Data.IBotListFetcher, Data.BotListFetcher>();

        // Register the atom-orchestrator
        services.AddBotDetectionOrchestrator();

        // Policy dispatch stack (ArmedRuleRegistry, SustainEvaluator, token bucket,
        // policy-decision log queue, IPolicyRuleStore YAML defaults, etc.). Called
        // from inside the module so every FOSS host inherits the dispatcher
        // without an extra opt-in step. Idempotent (all TryAdd).
        Policies.Dispatch.PolicyDispatchServiceExtensions.AddPolicyDispatcher(services);

        // Action policies (block / challenge / throttle / rate-limit / redirect
        // / log-only / silent-drop / sticky-deny / escalate-to-*). Registered
        // as IActionPolicy so ActionPolicyRegistry's ctor IEnumerable<IActionPolicy>
        // (additionalPolicies) picks them up alongside factory-materialised
        // policies from BotDetectionOptions.
        services.TryAddSingleton<Actions.IActionPolicyRegistry, Actions.ActionPolicyRegistry>();
        services.AddSingleton<Actions.IActionPolicyFactory, Actions.LogOnlyActionPolicyFactory>();
        services.AddSingleton<Actions.IActionPolicyFactory, Actions.BlockActionPolicyFactory>();
        services.AddSingleton<Actions.IActionPolicyFactory, Actions.ChallengeActionPolicyFactory>();
        services.AddSingleton<Actions.IActionPolicyFactory, Actions.ThrottleActionPolicyFactory>();
        services.AddSingleton<Actions.IActionPolicyFactory, Actions.RedirectActionPolicyFactory>();
        // Escalate-to-Learning / Session / LLM: one combined factory keyed
        // on ActionType.Escalate (registry uses ToDictionary(f => f.ActionType)
        // so only one factory per type). options["Target"] = learning | session
        // | llm picks the concrete class. See EscalateActionPolicyFactory.
        services.AddSingleton<Actions.IActionPolicyFactory, Actions.EscalateActionPolicyFactory>();

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

        // Threat intel: IP + CVE lookups feed the detection surface via
        // ThreatIntelAtom (priority 7, Wave 0). Four offline providers wired
        // by default; each self-gates on its Enabled flag so the FOSS-default
        // posture (master + all providers off) means zero HTTP + zero cache.
        // Operators opt in per provider under BotDetection:ThreatIntel:*.
        //
        // HttpClient is one shared factory-named client -- providers instantiate
        // it via IHttpClientFactory, one per provider, so a slow feed can't
        // starve the shared handler pool.
        services.AddHttpClient("threatintel").ConfigureHttpClient(c =>
            c.Timeout = TimeSpan.FromSeconds(30));
        services.TryAddSingleton<ThreatIntel.Providers.CisaKevProvider>(sp =>
            new ThreatIntel.Providers.CisaKevProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("threatintel"),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ThreatIntel.Providers.CisaKevProvider>>()));
        services.TryAddSingleton<ThreatIntel.Providers.CloudRangesProvider>(sp =>
            new ThreatIntel.Providers.CloudRangesProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("threatintel"),
                sp.GetRequiredService<Data.IBotListFetcher>(),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ThreatIntel.Providers.CloudRangesProvider>>()));
        services.TryAddSingleton<ThreatIntel.Providers.SpamhausDropProvider>(sp =>
            new ThreatIntel.Providers.SpamhausDropProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("threatintel"),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ThreatIntel.Providers.SpamhausDropProvider>>()));
        services.TryAddSingleton<ThreatIntel.Providers.TorExitProvider>(sp =>
            new ThreatIntel.Providers.TorExitProvider(
                sp.GetRequiredService<IHttpClientFactory>().CreateClient("threatintel"),
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<ThreatIntel.Providers.TorExitProvider>>()));
        services.AddSingleton<ThreatIntel.IThreatIntelProvider>(sp =>
            sp.GetRequiredService<ThreatIntel.Providers.CisaKevProvider>());
        services.AddSingleton<ThreatIntel.IThreatIntelProvider>(sp =>
            sp.GetRequiredService<ThreatIntel.Providers.CloudRangesProvider>());
        services.AddSingleton<ThreatIntel.IThreatIntelProvider>(sp =>
            sp.GetRequiredService<ThreatIntel.Providers.SpamhausDropProvider>());
        services.AddSingleton<ThreatIntel.IThreatIntelProvider>(sp =>
            sp.GetRequiredService<ThreatIntel.Providers.TorExitProvider>());
        services.TryAddSingleton<ThreatIntel.IThreatIntelCoordinator, ThreatIntel.ThreatIntelCoordinator>();
        services.TryAddSingleton<ThreatIntel.ThreatIntelEnrichmentQueue>();
        // ThreatIntelEnrichmentService subscribes to tick.10s on construction;
        // eager-resolved by BotDetectionHostedSingletonsBootstrap so the
        // subscription is active at boot even when no atom has resolved it yet.
        services.TryAddSingleton<ThreatIntel.ThreatIntelEnrichmentService>();
        // Refresh service runs the actual feed pulls. When master switch or all
        // providers are off, StartAsync/ExecuteAsync short-circuit; safe to
        // register unconditionally.
        services.AddHostedService<ThreatIntel.ThreatIntelRefreshService>();
        // Signal on successful refresh (task-#65 pattern). Payload-free: the
        // provider cache stays the source of truth; consumers re-lookup via
        // IThreatIntelCoordinator on notification.
        services.TryAddSingleton<Mostlylucid.Ephemeral.TypedSignalSink<ThreatIntel.ThreatIntelRefreshedSignal>>(sp =>
        {
            var inner = new Mostlylucid.Ephemeral.SignalSink(maxCapacity: 32, maxAge: TimeSpan.FromMinutes(15));
            return new Mostlylucid.Ephemeral.TypedSignalSink<ThreatIntel.ThreatIntelRefreshedSignal>(
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

        // Learning feedback-loop consumers. The Step-7 contributor delete
        // (1a8d2745) dropped these along with the contributors, but the atom
        // path still RAISES learning events (EscalateToLearningActionPolicy,
        // the LLM / intent coordinators) and LearningBackgroundService still
        // dispatches them to the registered ILearningEventHandler set. Without
        // these the loop raised into a void and IWeightStore (consumed by
        // HeuristicDetector + SignatureFeedbackHandler) was unresolved. Every
        // handler's dependencies are already registered above.
        services.TryAddSingleton<IWeightStore, SqliteWeightStore>();
        services.AddSingleton<ILearningEventHandler, InferenceHandler>();
        services.AddSingleton<ILearningEventHandler, PatternAccumulatorHandler>();
        services.AddSingleton<ILearningEventHandler, FeedbackHandler>();
        services.AddSingleton<ILearningEventHandler, DriftDetectionHandler>();
        services.AddSingleton<ILearningEventHandler, SignatureFeedbackHandler>();
        services.AddSingleton<ILearningEventHandler, Similarity.SimilarityLearningHandler>();
        services.AddSingleton<ILearningEventHandler, Similarity.IntentLearningHandler>();
        // ReputationMaintenanceService is a BackgroundService (periodic
        // decay / GC / persist of the pattern-reputation cache) AND a learning
        // handler. No data guardian covers pattern-reputation decay, so its
        // loop is not redundant. Register once, expose both roles.
        services.AddSingleton<ReputationMaintenanceService>();
        services.AddSingleton<ILearningEventHandler>(sp =>
            sp.GetRequiredService<ReputationMaintenanceService>());
        services.AddHostedService(sp => sp.GetRequiredService<ReputationMaintenanceService>());

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
        // Session echo — the two-phase eviction ack subscriber. FOSS default
        // is the null echo store; the next commit renames Data.IDetectionArchive
        // to IDetectionArchive and points ISessionEchoStore at it. Lazy-boots
        // on the first upsert like the other session atoms.
        services.TryAddSingleton<Orchestration.Sessions.ISessionEchoStore,
            Orchestration.Sessions.NullSessionEchoStore>();
        services.AddOnInitSignal<Orchestration.Sessions.SessionEchoAtom>(
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
