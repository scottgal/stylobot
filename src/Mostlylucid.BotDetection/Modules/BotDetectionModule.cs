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
using Mostlylucid.BotDetection.WebBotAuth;
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

        // Web Bot Auth foundation: public-key registry (Coordinator) + Tick1h
        // refresh + durability Escalator, and the RFC 9421 / license-capability
        // token verifier. FOSS capability; self-gates on
        // BotDetection:PublicKeyRegistry:Enabled (default false).
        services.AddWebBotAuth();

        // WebBotAuth approval atom options (knobs live under BotDetection:WebBotAuth).
        services.AddOptions<Auth.WebBotAuthOptions>()
            .BindConfiguration("BotDetection:WebBotAuth");

        // Manifest loader + config provider back every detector atom's
        // per-manifest weights / thresholds / feature flags. FOSS ships the
        // YAML manifests as embedded resources under Orchestration/Manifests/.
        services.TryAddSingleton<Orchestration.Manifests.DetectorManifestLoader>();
        services.TryAddSingleton<Orchestration.Manifests.IDetectorConfigProvider,
            Orchestration.Manifests.DetectorConfigProvider>();

        // FOSS config subsystem — dropped by the Step-7 contributor delete (1a8d2745)
        // with the contributors, but every class survived. The dashboard's
        // Configuration tab resolves IConfigEditorService; its Razor partial is typed
        // to ConfigurationEditorModel, so when the service is missing the shell falls
        // through to that partial with the wrong model and the route 500s.
        // FileSystemConfigurationOverrideSource watches {ContentRoot}/stylobot-config
        // for YAML/JSON edits (hot-reload); ConfigurationWatcher invalidates the config
        // cache when any override source changes; ConfigEditorService reads embedded
        // manifests + overrides for the tab. Lambda registration keeps IHostEnvironment
        // optional so plain-ServiceCollection test fixtures can still resolve it.
        services.TryAddSingleton(sp => new Orchestration.Manifests.FileSystemConfigurationOverrideSource(
            sp.GetRequiredService<Orchestration.Manifests.DetectorManifestLoader>(),
            sp.GetService<Microsoft.Extensions.Hosting.IHostEnvironment>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Orchestration.Manifests.FileSystemConfigurationOverrideSource>>()));
        services.AddSingleton<Orchestration.Manifests.IConfigurationOverrideSource>(sp =>
            sp.GetRequiredService<Orchestration.Manifests.FileSystemConfigurationOverrideSource>());
        services.AddHostedService(sp =>
            sp.GetRequiredService<Orchestration.Manifests.FileSystemConfigurationOverrideSource>());
        services.AddHostedService<Orchestration.Manifests.ConfigurationWatcher>();
        services.TryAddSingleton<Orchestration.Manifests.ConfigEditorService>();
        services.TryAddSingleton<Orchestration.Manifests.IConfigEditorService>(
            sp => sp.GetRequiredService<Orchestration.Manifests.ConfigEditorService>());

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

        // Domain/host normalization (eTLD+1 via the embedded Public Suffix List,
        // SNI-preferred over the spoofable Host header). Backs RequestScope --
        // the (Domain, Host) pair BotDetectionMiddleware stamps on HttpContext.Items
        // for every downstream reader (DetectionBroadcastMiddleware's dashboard
        // partition key, FingerprintMatchAtom, SiteProfiles). Was previously
        // referenced by ISiteProfileResolver / IEffectivePolicyResolver /
        // ScopedRateLimitResolver but never registered anywhere, so DomainNormalizer
        // could never resolve and every consumer silently degraded to "unknown".
        services.AddOptions<Domains.DomainNormalizerOptions>()
            .BindConfiguration(Domains.DomainNormalizerOptions.SectionName);
        services.TryAddSingleton<Domains.PublicSuffixList>(_ => Domains.PublicSuffixList.LoadEmbedded());
        services.TryAddSingleton<Domains.DomainNormalizer>();

        // Site profiles: per-host honeypot/policy profile selection (BotDetection:Sites).
        // Same silent-drop class as DomainNormalizerOptions above: SiteMapOptions /
        // ISiteProfileCatalog / ISiteProfileResolver were fully built, unit tested (via
        // direct construction, which is why no test caught this), and consumed
        // (HoneypotPathTagger's optional profile-resolver parameter) but never
        // registered anywhere, so every host ran with an empty SiteMapOptions
        // regardless of configured BotDetection:Sites and per-host profile promotion
        // never fired.
        services.AddOptions<SiteProfiles.SiteMapOptions>()
            .BindConfiguration(SiteProfiles.SiteMapOptions.SectionName);
        services.TryAddSingleton<SiteProfiles.ISiteProfileCatalog, SiteProfiles.SiteProfileCatalog>();
        services.TryAddSingleton<SiteProfiles.ISiteProfileResolver, SiteProfiles.SiteProfileResolver>();

        // Passive upstream-health tracking: DegradationAtom is the in-process
        // EWMA of real response status codes (fed by BotDetectionMiddleware
        // post-_next), UpstreamHealthGate reads it to stand down status-derived
        // bot-signal contributors during an outage. Both were defined and unit
        // tested but never registered, so the aggregate 5xx/4xx rate was never
        // fed from real traffic and the dashboard's site-health card always
        // showed the empty-data default. In-process only per the transient
        // per-request state carve-out; DegradationStoreSampler (UI) persists
        // periodic snapshots via IDashboardEventStore for dashboard history.
        services.TryAddSingleton<RateLimit.DegradationAtom>();
        services.TryAddSingleton<RateLimit.UpstreamHealthGate>();
        // Fingerprint store — FOSS default is SQLite (SqliteFingerprintStore),
        // which the BrowserModes subsystem also depends on directly. Commercial
        // pack replaces with a shared-schema Postgres variant via TryAdd-loses.
        services.TryAddSingleton<Identity.SqliteFingerprintStore>();
        services.TryAddSingleton<Identity.IFingerprintStore>(
            sp => sp.GetRequiredService<Identity.SqliteFingerprintStore>());
        // IFingerprintReader is the read-only facet of IFingerprintStore. Bind it
        // to whatever store is active (SQLite here; Postgres via the commercial
        // pack's own AddSingleton) so read-only consumers — notably the UI's
        // BotDetectionDetailsViewComponent — resolve without pulling the full
        // store. Without this the FOSS-only host (e.g. the SQLite demo) throws
        // "Unable to resolve IFingerprintReader" activating that ViewComponent.
        services.TryAddSingleton<Identity.IFingerprintReader>(
            sp => sp.GetRequiredService<Identity.IFingerprintStore>());
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
        // Self-register the concrete brute-force KNN builder so any downstream pack that
        // swaps IKnnGraphBuilder for a resolver whose fallback is BruteForceKnnGraphBuilder
        // can resolve it from DI (it was declared but never registered; the Postgres pack
        // had to work around the gap). Stateless, safe to always have available.
        services.TryAddSingleton<Clustering.Knn.BruteForceKnnGraphBuilder>();
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
        // Browser-characteristic consistency (browser_char): clones the browser_mode
        // catalogue on the same identity_archetypes table (catalogue_kind='browser_char').
        // Hardcoded seed prior is the slice-1 stand-in (YAML source is a follow-up, exactly
        // like HardcodedBrowserModeSeedSource was before the YAML loader).
        services.TryAddSingleton<Identity.BrowserChar.IBrowserCharSeedSource>(sp =>
            new Identity.BrowserChar.HardcodedBrowserCharSeedSource(
                sp.GetRequiredService<Identity.IdentityVectorLayout>()));
        services.TryAddSingleton<Identity.BrowserChar.BrowserCharCentroidCatalogue>();
        // Materialize the scorer once at boot (same blocking-await pattern as the mode
        // classifier above); it snapshots the centroids + builds the engine-weighted mask.
        services.TryAddSingleton<Identity.BrowserChar.BrowserCharConsistencyScorer>(sp =>
        {
            var cat = sp.GetRequiredService<Identity.BrowserChar.BrowserCharCentroidCatalogue>();
            var layout = sp.GetRequiredService<Identity.IdentityVectorLayout>();
            var bc = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Models.BotDetectionOptions>>()
                .Value.Identity.BrowserChar;
            return Identity.BrowserChar.BrowserCharConsistencyScorer
                .LoadAsync(cat, layout, bc.EngineWeight, bc.FeatureWeight).GetAwaiter().GetResult();
        });
        // Signature scoring / dashboard aggregate service (also read by
        // SignatureAtom for verdict composition).
        services.TryAddSingleton<Dashboard.MultiFactorSignatureService>();
        // Waveform history is a per-fingerprint ring buffer feeding
        // BehavioralWaveformAtom's spectral analysis. Bind the interface to the
        // concrete so the commercial pack can Replace the durable tier with a
        // Postgres impl (one durable backend, never memory-only). Consumers inject
        // Orchestration.Atoms.IWaveformHistoryStore, not the concrete.
        services.TryAddSingleton<Orchestration.Atoms.WaveformHistoryStore>();
        services.TryAddSingleton<Orchestration.Atoms.IWaveformHistoryStore>(
            sp => sp.GetRequiredService<Orchestration.Atoms.WaveformHistoryStore>());
        // Detectors (internal helpers — injected by atoms that wrap them).
        // Three atoms need real DI-backed dependencies that can't be null'd:
        // BehavioralAtom needs IMemoryCache, VersionAgeAtom needs IBrowserVersionService,
        // ClientSideAtom needs IBrowserFingerprintStore. Self-instantiating with null
        // for these caused an NRE storm on prod (run193, 460+ NREs in 3 min, SIGSEGV).
        services.TryAddSingleton<Detectors.BehavioralDetector>();
        services.TryAddSingleton<Detectors.VersionAgeDetector>();
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
        services.TryAddSingleton<Definitions.TlsReference.IJa3ReferenceIndex, Definitions.TlsReference.Ja3ReferenceIndex>();
        services.TryAddSingleton<Services.UaProfileStore>();
        services.TryAddSingleton<Services.CountryReputationTracker>();
        services.TryAddSingleton<Services.ReactiveSignalTracker>();
        services.TryAddSingleton<Services.BotClusterService>();
        services.AddHostedService(sp => sp.GetRequiredService<Services.BotClusterService>());
        services.TryAddSingleton<Analysis.DeploymentNormTracker>();
        // Legacy Analysis.SessionStore (per-session sliding window of vectors)
        // still used by SessionVectorAtom. Distinct from the new
        // Orchestration.Sessions.SessionStore (shared per-domain sink).
        services.TryAddSingleton<Analysis.SessionStore>(sp =>
        {
            // Read from config or env: BotDetection__SessionStore__ActiveSignaturesMaxEntries
            var max = 20_000;
            var cfg = sp.GetService<Microsoft.Extensions.Configuration.IConfiguration>();
            if (cfg is not null && int.TryParse(cfg["BotDetection:SessionStore:ActiveSignaturesMaxEntries"], out var cfgMax) && cfgMax > 0)
                max = cfgMax;
            else if (int.TryParse(Environment.GetEnvironmentVariable("STYLOBOT_SESSION_STORE_MAX_SIGNATURES"), out var envMax) && envMax > 0)
                max = envMax;

            // Session-idle finalize (deploy- 2026-08-15): the PERIOD SINCE THE LAST
            // REQUEST, actively swept (FinalizeIdleSessionsAsync on a cadence).
            // Minutes; 0 disables. Default 30.
            TimeSpan? maxIdle = TimeSpan.FromMinutes(30);
            if (cfg is not null && int.TryParse(cfg["BotDetection:SessionStore:MaxSessionIdleMinutes"], out var cfgIdle))
                maxIdle = cfgIdle > 0 ? TimeSpan.FromMinutes(cfgIdle) : null;

            // SessionMaxLifetime — the continuous-class chunk boundary (operator
            // 2026-08-15: even continuous sessions chunk — the never-idle class the
            // idle sweep can never catch because nothing idles). Config or env.
            var maxLifetime = TimeSpan.FromMinutes(30);
            if (cfg is not null && TimeSpan.TryParse(cfg["BotDetection:SessionStore:SessionMaxLifetime"], out var cfgLife) && cfgLife > TimeSpan.Zero)
                maxLifetime = cfgLife;
            else if (TimeSpan.TryParse(Environment.GetEnvironmentVariable("STYLOBOT_SESSION_STORE_MAX_LIFETIME"), out var envLife) && envLife > TimeSpan.Zero)
                maxLifetime = envLife;
            return new Analysis.SessionStore(
                sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<Analysis.SessionStore>>(),
                activeSignaturesMaxEntries: max,
                maxSessionIdle: maxIdle,
                sessionMaxLifetime: maxLifetime);
        });
        // Client-side fingerprint population tracker (browser fingerprint
        // observed-value distribution).
        services.TryAddSingleton<ClientSide.FingerprintPopulationTracker>();
        // Centroid stores: real WriteBehindLfuStore SQLite stores are the FOSS default.
        // The concrete class is registered first so both the interface binding and
        // StoreInitService<T> resolve to the same singleton. StoreInitService creates
        // the table schema at host startup AND eagerly resolves the store, which starts
        // the drain loop (WriteBehindLfuStore ctor). AddBotDetectionInMemory replaces
        // the interface bindings with their Null variants via services.Replace so
        // Slim* and SessionVectorWarmupService see no-op stores in ephemeral mode.
        services.TryAddSingleton<Data.SqliteSignatureCentroidStore>();
        services.TryAddSingleton<Data.Contracts.ISignatureCentroidStore>(
            sp => sp.GetRequiredService<Data.SqliteSignatureCentroidStore>());
        services.AddHostedService<Storage.StoreInitService<Data.SqliteSignatureCentroidStore>>();

        services.TryAddSingleton<Data.SqliteSessionCentroidStore>();
        services.TryAddSingleton<Data.Contracts.ISessionCentroidStore>(
            sp => sp.GetRequiredService<Data.SqliteSessionCentroidStore>());
        services.AddHostedService<Storage.StoreInitService<Data.SqliteSessionCentroidStore>>();

        services.TryAddSingleton<Data.SqliteIntentCentroidStore>();
        services.TryAddSingleton<Data.Contracts.IIntentCentroidStore>(
            sp => sp.GetRequiredService<Data.SqliteIntentCentroidStore>());
        services.AddHostedService<Storage.StoreInitService<Data.SqliteIntentCentroidStore>>();
        // Identity archetype registry (loaded from embedded YAML archetypes).
        services.TryAddSingleton<Identity.IdentityArchetypeRegistry>();
        // Centroid sequence store — per-fingerprint sequence-of-centroids
        // history fed to ContentSequenceAtom.
        services.TryAddSingleton<Services.CentroidSequenceStore>();
        // Endpoint divergence tracker — per-fingerprint deviation-from-expected
        // endpoint access pattern; feeds ContentSequenceAtom.
        services.TryAddSingleton<Services.EndpointDivergenceTracker>();
        // Identity global weights cache (per-slot weight decay + updates).
        services.TryAddSingleton<Identity.IdentityGlobalWeightsCache>();
        // IdentityProcessingCoordinator — Pass-2 coalescer for FingerprintMatchAtom AND the
        // sequencer for FingerprintAbsorptionService's debounced folds. It is a real
        // BackgroundService whose ExecuteAsync spins the worker loops that drain the queue, so
        // it MUST be hosted as well as injectable. The Step-7 contributor delete (1a8d2745)
        // dropped the AddHostedService while keeping the singleton, so its worker loops never
        // started: RunAsync enqueued, the queue filled to MaxQueueDepth, then every Pass-2
        // sheds -> identity confirm silently degraded to L1-only in prod, and any work routed
        // through it (absorption) would never drain. Restore the hosted registration. Asserted
        // by IdentityProcessingCoordinatorHostedRegistrationTests so it cannot silently recur.
        services.TryAddSingleton<Identity.IdentityProcessingCoordinator>();
        services.AddHostedService(sp => sp.GetRequiredService<Identity.IdentityProcessingCoordinator>());

        // Tick-driven identity learning loop. These were registered in the old
        // ServiceCollectionExtensions and the Step-7 contributor delete (1a8d2745,
        // ServiceCollectionExtensions 1778 -> 156 lines) dropped the registrations while
        // LEAVING BotDetectionHostedSingletonsBootstrap eager-resolving them via GetService,
        // which returned null silently -- so the entire learning loop (absorption, drift,
        // calibration, entity resolution, convergence, markov, mode-absorption, rollup) has
        // been DEAD on main. Absorption never running is the root cause of the identity
        // observation leak (observations pile up unabsorbed because nothing folds them into
        // centroids). Restore them (matching the pre-drop AddSingleton/TryAddSingleton set).
        services.TryAddSingleton<Identity.FingerprintAbsorptionService>();
        services.TryAddSingleton<Identity.BrowserModes.FingerprintModeAbsorptionService>();
        services.TryAddSingleton<Identity.BrowserModes.FingerprintRollupRecomputeService>();
        services.TryAddSingleton<Identity.FingerprintDriftService>();
        services.TryAddSingleton<Identity.IdentityWeightCalibrationService>();
        services.TryAddSingleton<Services.DeploymentNormCalibrationService>();
        services.TryAddSingleton<Services.EntityResolutionService>();
        services.TryAddSingleton<Markov.MarkovTracker>();
        services.TryAddSingleton<Markov.PopulationMarkovService>();
        services.TryAddSingleton<SignatureConvergenceService>();
        // Func<DbConnection> — FOSS default SQLite connection factory.
        // BotDetectionOptions.DatabasePath drives the file path; empty means
        // a shared in-memory DB (fresh per process). Commercial Postgres pack
        // overrides via TryAdd-loses at its Add* site.
        services.TryAddSingleton<Func<System.Data.Common.DbConnection>>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>().Value;
            var connString = string.IsNullOrEmpty(opts.DatabasePath)
                ? "Data Source=file::memory:?cache=shared;Mode=Memory;Cache=Shared"
                : $"Data Source={opts.DatabasePath};Cache=Shared;Pooling=true";
            return () => new Microsoft.Data.Sqlite.SqliteConnection(connString);
        });
        // SignatureCoordinator — per-fingerprint verdict aggregator that
        // multiple atoms read. Has an options block.
        services.AddOptions<Orchestration.SignatureCoordinatorOptions>()
            .BindConfiguration("BotDetection:SignatureCoordinator");
        services.TryAddSingleton<Orchestration.SignatureCoordinator>();
        // Operator "correct decision" corrections (in-memory prior, nullable seam). The
        // commercial correction store calls Set(...) on write; the verdict composer reads it
        // as a bias, never an override. Default in-memory impl; nothing populates it under FOSS.
        services.TryAddSingleton<Risk.IOperatorCorrectionPriors, Risk.InMemoryOperatorCorrectionPriors>();
        // Neutral host-posture seam (nullable/plugin pattern): a host may register its own
        // IDetectionPostureProvider (e.g. commercial's licensing layer, reading ILicenseState)
        // BEFORE AddBotDetection/AddStyloBot runs to globally gate learning writes / force
        // observe-only enforcement, for whatever reason the host cares about. FOSS never
        // knows why -- standalone FOSS with nothing registered gets Full (no behaviour change).
        services.TryAddSingleton<Posture.IDetectionPostureProvider, Posture.FullDetectionPostureProvider>();
        // SignatureCoordinatorWarmupService replays recently persisted request records into the
        // SignatureCoordinator (and MarkovTracker) at startup so clustering resumes from a warm
        // corpus instead of cold on every restart. The Step-7 contributor delete (1a8d2745)
        // dropped this AddHostedService (same drop as IdentityProcessingCoordinator), so since
        // then clustering has cold-started from live traffic only after every deploy/restart.
        // Restore it. ExecuteAsync fails open (logs, no boot impact). Asserted by
        // Step7HostedServiceRegistrationTests so it cannot silently recur.
        services.AddHostedService<Services.SignatureCoordinatorWarmupService>();
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
        // (#16) IdentityChange's per-fingerprint surface-dim drift lookback now rides the
        // bounded IFingerprintStore hot cache (SurfaceDims, co-evicted, never persisted) —
        // no separate FingerprintDimSnapshotCache singleton to register.
        // Similarity search — FOSS defaults use in-memory sliding-window impls;
        // commercial pgvector pack swaps via TryAdd-loses.
        services.TryAddSingleton<Similarity.IIntentSimilaritySearch, Similarity.SlimIntentSearch>();
        services.TryAddSingleton<Similarity.ISignatureSimilaritySearch, Similarity.SlimSignatureSimilaritySearch>();
        // Simulation pack registry (BDF replay + adversarial sim harness).
        services.TryAddSingleton<SimulationPacks.ISimulationPackRegistry,
            SimulationPacks.SimulationPackLoader>();
        // Health-endpoint catalog — built once from options; used by HealthEndpointAtom.
        // Operators configure BotDetection:HealthEndpoints:Paths to extend or replace the
        // default set of ten Kubernetes / cloud-provider probe paths. Paths starts empty so
        // that configuration binding replaces (not appends to) the defaults. PostConfigure
        // fills the ten standard paths only when the operator has not supplied any.
        services.AddOptions<HealthEndpoints.HealthEndpointOptions>()
            .BindConfiguration(HealthEndpoints.HealthEndpointOptions.SectionName)
            .PostConfigure(opts =>
            {
                if (opts.Paths.Count == 0)
                    opts.Paths.AddRange(HealthEndpoints.HealthEndpointOptions.DefaultPaths);
            });
        services.TryAddSingleton<HealthEndpoints.HealthEndpointCatalog>();
        // Internal-plumbing catalog — built once from options; used by InternalPlumbingAtom.
        // The product's OWN endpoints (the SignalR dashboard hub + the client-side
        // fingerprint beacon) classify as BotType.Internal so the dashboard's live-update
        // channel never reads as a high-threat visitor. Paths starts empty so that
        // configuration binding replaces (not appends to) the defaults. PostConfigure
        // fills the standard paths only when the operator has not supplied any — a host
        // serving the hub at a custom path MUST list it here (e.g. via STYLOBOT_HUB_PATH).
        services.AddOptions<InternalPlumbing.InternalPlumbingOptions>()
            .BindConfiguration(InternalPlumbing.InternalPlumbingOptions.SectionName)
            .PostConfigure(opts =>
            {
                if (opts.Paths.Count == 0)
                    opts.Paths.AddRange(InternalPlumbing.InternalPlumbingOptions.DefaultPaths);
            });
        services.TryAddSingleton<InternalPlumbing.InternalPlumbingCatalog>();
        // PII hasher — FOSS default seeds with a fixed key so DI resolves;
        // operators override via their own AddSingleton<PiiHasher>(sp => ...).
        // Commercial (licensing pack) replaces with a JWT-derived key.
        services.TryAddSingleton<Privacy.PiiHasher>(_ =>
            new Privacy.PiiHasher(
                System.Text.Encoding.UTF8.GetBytes("stylobot-foss-default-key-rotate-per-deployment")));
        // Webhook endpoint reputation — FOSS default is SQLite (webhooks.db alongside
        // fingerprints.db, derived from the same DatabasePath directory). The source IP
        // is hashed with the SAME keyed PiiHasher used for every other PII signature in
        // the product (not a standalone unkeyed hash) so the low-entropy IPv4 space
        // can't be brute-forced offline. AddBotDetectionInMemory (ephemeral mode) swaps
        // this to NullWebhookEndpointReputation.
        services.TryAddSingleton<Reputation.IWebhookEndpointReputation>(sp =>
        {
            var opts = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<BotDetectionOptions>>().Value;
            var baseDbPath = string.IsNullOrEmpty(opts.DatabasePath)
                ? Path.Combine(AppContext.BaseDirectory, "botdetection.db")
                : opts.DatabasePath;
            var dataDir = Path.GetDirectoryName(baseDbPath) ?? AppContext.BaseDirectory;
            var webhooksDb = Path.Combine(dataDir, "webhooks.db");
            var hasher = sp.GetRequiredService<Privacy.PiiHasher>();
            return new Data.SqliteWebhookReputationStore(
                webhooksDb, Definitions.Webhooks.WebhookCatalog.Default, hasher);
        });
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
        // / log-only / silent-drop / sticky-deny / escalate-to-* / allow). Registered
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
        // Allow: pass-through policy used by endpoint-policy rules that
        // explicitly allow a request class (e.g. internal health probes).
        // Detection still runs; only the pre-detection endpoint gate is satisfied.
        services.AddSingleton<Actions.IActionPolicy>(Actions.AllowActionPolicy.Instance);

        // Endpoint-policy options: bind from config then append built-in default
        // health-probe rules at the END so operator-declared rules take precedence
        // via first-match-wins. The defaults allow internal-source callers through
        // on the ten well-known health paths without blocking or delaying them.
        // This is the only registration of EndpointPolicyOptions; ConfigEndpointPolicyResolver
        // reads it via IOptionsMonitor so operator appsettings overrides are picked up
        // on the next Recompile without a process restart.
        services.AddOptions<EndpointPolicies.EndpointPolicyOptions>()
            .BindConfiguration(EndpointPolicies.EndpointPolicyOptions.SectionName)
            // Recover operator config keys that aren't baked-in matchers into
            // rule.Extensions so external IEndpointPolicyRuleExtension matchers
            // (commercial) can consume them. Runs before the health-default
            // append below so it only walks config-declared rules.
            .PostConfigure<Microsoft.Extensions.Configuration.IConfiguration>(
                EndpointPolicies.EndpointPolicyRuleExtensionsBinder.Collect)
            .PostConfigure(opts =>
            {
                foreach (var path in HealthEndpoints.HealthEndpointOptions.DefaultPaths)
                {
                    opts.Rules.Add(new EndpointPolicies.EndpointPolicyRule
                    {
                        Path = path,
                        Source = "internal",
                        Action = "allow",
                        Reason = "health-probe-default"
                    });
                }
            });
        // Endpoint-policy resolver: maps IOptions<EndpointPolicyOptions> to
        // a compiled rule list (startup snapshot -- FOSS has no runtime
        // options-reload). Optional IOptions<BotDetectionOptions> enables
        // the TrustedProxyIps check in the Source matcher.
        services.TryAddSingleton<EndpointPolicies.IEndpointPolicyResolver>(sp =>
            new EndpointPolicies.ConfigEndpointPolicyResolver(
                sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<EndpointPolicies.EndpointPolicyOptions>>(),
                sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<EndpointPolicies.ConfigEndpointPolicyResolver>>(),
                sp.GetService<Identity.BrowserModes.IBrowserModeResolver>(),
                sp.GetService<Microsoft.Extensions.Options.IOptions<Models.BotDetectionOptions>>(),
                // Zero extensions in FOSS: GetServices returns empty and the seam
                // stays inert. Commercial packages register their own matchers.
                sp.GetServices<EndpointPolicies.IEndpointPolicyRuleExtension>()));

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

        // WellKnownBots: the ~635-entry arcjet catalog that names + classifies known bots.
        // TryAddSingleton so UserAgentAtom / AiScraperAtom / the middleware UA fallback share ONE
        // index with the refresh service. The refresh service SEEDS the embedded baseline on
        // startup (so the catalog is populated even air-gapped) then refreshes from the arcjet
        // URL per RefreshInterval; empty URL just skips the download.
        //
        // REGRESSION FIX: the atom refactor dropped both registrations. The atoms take the index
        // as an OPTIONAL ctor param (WellKnownBotIndex? = null), so it silently resolved to null,
        // the `if (_wellKnownBots is { Count: > 0 })` catalog branch never ran, and every
        // catalog-only bot (PetalBot, ...) fell through to "appears normal" human. Guarded by
        // Step7HostedServiceRegistrationTests.WellKnownBot*.
        // Both singletons. The refresh service is IDisposable + coordinator-tick (NOT
        // IHostedService); its ctor seeds the baseline + subscribes to the refresh tick, and
        // BotDetectionHostedSingletonsBootstrap already eager-resolves it at boot (the GetService
        // call there was orphaned when this registration was dropped). Registering the singleton
        // re-arms that existing eager-resolve.
        services.TryAddSingleton<Definitions.WellKnownBots.WellKnownBotIndex>();
        services.TryAddSingleton<Definitions.WellKnownBots.WellKnownBotRefreshService>();
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
        // Signals-native session layer (Slice 1). Registered BEFORE SessionStore so
        // its optional ctor param picks up the registry and Upsert dual-writes.
        services.AddOptions<Orchestration.Sessions.SessionCoordinatorOptions>()
            .BindConfiguration("BotDetection:SessionCoordinator");
        services.TryAddSingleton<Orchestration.Sessions.SiteCoordinatorRegistry>();
        services.TryAddSingleton<Orchestration.Sessions.SessionStore>();
        // The session-idle sweep's cadence driver (2026-08-15): finalizes sessions
        // whose last contribution is past MaxIdle on Tick10s — the stopped/paused
        // class the gap boundary and the sliding TTL never catch.
        services.AddHostedService<Orchestration.Sessions.SessionIdleSweepHostedService>();
        services.AddOnInitSignal<Orchestration.Sessions.SessionAtom>(
            Orchestration.Sessions.SessionStoreOptions.InitSignal);
        services.AddOnInitSignal<Orchestration.Sessions.SessionPersistenceAtom>(
            Orchestration.Sessions.SessionStoreOptions.InitSignal);
        // Detection archive — the durable session/signature/request archive.
        // FOSS default is SQLite (zero-dependency, ships in-process). Commercial
        // Postgres pack replaces via TryAdd-loses at its own Add* site;
        // AddStyloBotDashboardRemote replaces with RemoteDetectionArchive for
        // remote-mode dashboards.
        services.TryAddSingleton<Data.IDetectionArchive, Data.SqliteDetectionArchive>();

        // The legacy session finalize → row path (stream- ruling 2026-08-15 — the
        // un-gate is the canonical row path; the atomizer routing retired with its
        // requests-table input): SessionPersistenceService drains the
        // Analysis.SessionStore's SessionFinalized (the guard-fixed atom's finalize
        // set — gap/chunk/idle/eviction) into AddSessionAsync. Was never registered
        // — only bootstrap-eager-resolved.
        services.TryAddSingleton<Data.SessionPersistenceService>();
        services.AddHostedService<Data.SessionPersistenceLifecycleHostedService>();

        // Data guardian: prune stale time-series bucket rows (Phase 1, extracted
        // from VectorCompactionService). Runs on its own interval; interval and
        // enabled flag are config-driven via BotDetection:Guardians:BucketRetention:*.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Guardians.IGuardian, Guardians.BucketRetentionGuardian>());

        // Data guardian: compact overflowing SQLite session rows into behavioural
        // centroids (Phase 2, extracted from VectorCompactionService). Runs on its
        // own interval; config-driven via BotDetection:Guardians:SessionCompaction:*.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Guardians.IGuardian, Guardians.SessionCompactionGuardian>());

        // Data guardian: compact the HNSW session vector index when it exceeds
        // threshold (Phase 3, extracted from VectorCompactionService). L1 collapses
        // multi-vector signatures to per-signature centroids; L2 merges low-priority
        // cluster members into cluster centroids. No-op when ISessionVectorSearch is
        // not registered. Config-driven via BotDetection:Guardians:HnswCompaction:*.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Guardians.IGuardian, Guardians.HnswCompactionGuardian>());

        // Data guardian: prune stale rows from all three centroid tables (Phase 4,
        // extracted from VectorCompactionService). Prunes signature, session, and
        // intent centroids older than SelfMaintenance:CentroidRetentionDays in
        // parallel. Config-driven via BotDetection:Guardians:CentroidRetention:*.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Guardians.IGuardian, Guardians.CentroidRetentionGuardian>());

        // Data guardian: cross-signature cap enforcement (Phase 5, extracted from
        // VectorCompactionService). Evicts lowest-value signatures by
        // DecisionNecessity when distinct signatures exceed MaxSignatures.
        // No-op when MaxSignatures == 0 (the default, unlimited).
        // Config-driven via BotDetection:Guardians:SignatureCap:*.
        services.TryAddEnumerable(ServiceDescriptor.Singleton<Guardians.IGuardian, Guardians.SignatureCapGuardian>());

        // Identity data guardians (Part B): bound fingerprints.db, the one durable
        // store the vector guardians above don't cover. They operate on
        // SqliteFingerprintStore, which is dormant unless Identity:Enabled, so they
        // register ONLY when identity is on -- keeping the guardian roster honest
        // (5 vector guardians when identity is off, 7 when on).
        if (IsIdentityEnabled(services))
        {
            // Prune absorbed fingerprint_observations, keeping all unabsorbed plus
            // the most-recent-K per fingerprint (drift-preserving). Config-driven via
            // BotDetection:Guardians:FingerprintObservationRetention:*.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Guardians.IGuardian,
                Identity.FingerprintObservationRetentionGuardian>());

            // Cross-fingerprint MemoryAdaptiveCap eviction by DecisionNecessity;
            // cascade-deletes all per-fp tables and never evicts verified claims.
            // No-op when MaxFingerprints == 0. Config-driven via
            // BotDetection:Guardians:FingerprintEviction:*.
            services.TryAddEnumerable(ServiceDescriptor.Singleton<Guardians.IGuardian,
                Identity.FingerprintEvictionGuardian>());
        }

        // The walker itself. It collects every IGuardian above via IEnumerable and
        // runs each on its own interval off the ScheduleCoordinator's Tick1m. This
        // registration was MISSING: the guardians were registered but nothing walked
        // them, so the whole tier silently never ran (GuardianService is eager-resolved
        // by BotDetectionHostedSingletonsBootstrap via GetService, which returned null
        // when it was unregistered). Register it so the guardians actually fire.
        services.TryAddSingleton<Guardians.GuardianService>();

        // Session echo — the two-phase eviction ack subscriber. Routes to
        // whatever IDetectionArchive is registered (SqliteDetectionArchive
        // by default, PostgreSQLDetectionArchive via commercial pack Replace).
        // Any host that wires an archive automatically gets echo persistence
        // flowing through it. Test hosts override with a fake
        // ISessionEchoStore via a TryAdd that beats this registration to
        // the punch. Lazy-boots on the first session upsert.
        services.TryAddSingleton<Orchestration.Sessions.ISessionEchoStore,
            Orchestration.Sessions.DetectionArchiveEchoStore>();
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
            .ValidateDataAnnotations()
            // Fail-fast on an unconfigured DatabasePath. StyloBot persists
            // detection / identity / session state to SQLite and REQUIRES a path.
            // Leaving it null used to fall back SILENTLY to an in-memory database
            // (Data Source=file::memory:), which grows UNBOUNDED under load and OOMs
            // the process (it reads exactly like a memory leak; found via soak+load).
            // null == "operator forgot to configure" -> throw loud. AddBotDetectionInMemory
            // sets DatabasePath to string.Empty as the EXPLICIT opt-in signal, so empty
            // is allowed and only null is rejected. No silent in-memory fallback.
            .Validate(
                o => o.DatabasePath is not null,
                "BotDetection:DatabasePath is not configured. StyloBot persists to SQLite and requires a database " +
                "path (e.g. \"botdetection.db\" or an absolute path). Leaving it unset previously fell back silently " +
                "to an in-memory database that grows UNBOUNDED under load and OOMs the process. If you intend " +
                "ephemeral / in-memory operation (tests, CI, throwaway hosts), call AddBotDetectionInMemory() " +
                "explicitly (it sets DatabasePath to empty to signal that intent). Do NOT leave DatabasePath null.")
            .ValidateOnStart();
    }

    /// <summary>
    ///     Resolves the effective <c>Identity.Enabled</c> flag at DI-registration
    ///     time so the identity data guardians register only when the metastable
    ///     identity layer is on. Materialises a <see cref="BotDetectionOptions"/>
    ///     from the same two sources the options system uses: the
    ///     <c>BotDetection</c> configuration section (the config form) and every
    ///     registered <see cref="Microsoft.Extensions.Options.IConfigureOptions{BotDetectionOptions}"/>
    ///     delegate (the <c>AddBotDetection(configure)</c> form), so both surfaces
    ///     are honoured without building the full service provider.
    /// </summary>
    private static bool IsIdentityEnabled(IServiceCollection services)
    {
        var options = new BotDetectionOptions();

        // 1. Config-bind form: BotDetection:Identity:Enabled.
        var configuration = services
            .LastOrDefault(d =>
                d.ServiceType == typeof(Microsoft.Extensions.Configuration.IConfiguration))
            ?.ImplementationInstance as Microsoft.Extensions.Configuration.IConfiguration;
        if (configuration is not null)
            Microsoft.Extensions.Configuration.ConfigurationBinder.Bind(
                configuration.GetSection("BotDetection"), options);

        // 2. configure-action form: apply any registered IConfigureOptions delegates
        //    (Configure<BotDetectionOptions> registers ConfigureNamedOptions here).
        foreach (var descriptor in services.Where(d =>
                     d.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<BotDetectionOptions>)))
        {
            if (descriptor.ImplementationInstance is
                Microsoft.Extensions.Options.IConfigureOptions<BotDetectionOptions> configure)
            {
                configure.Configure(options);
            }
        }

        return options.Identity.Enabled;
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
