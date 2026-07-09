using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MEOptions = Microsoft.Extensions.Options.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.OpenApi;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Atoms;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Auth;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.Auth;
using Mostlylucid.BotDetection.UI.Services.Dashboard;
using Mostlylucid.BotDetection.UI.Services.Routes;
using Mostlylucid.Notify.DependencyInjection;

namespace Mostlylucid.BotDetection.UI.Extensions;

/// <summary>
///     Extension methods for registering Stylobot Dashboard services.
/// </summary>
public static class StyloBotDashboardServiceExtensions
{
    /// <summary>
    ///     Adds StyloBot UI services (tag helpers, view components, detection data extraction)
    ///     WITHOUT the full dashboard route or SignalR hub.
    ///     <para>
    ///     Use this when you want to embed individual StyloBot widgets in your own pages
    ///     using tag helpers like <c>&lt;sb-badge /&gt;</c>, <c>&lt;sb-gate&gt;</c>, etc.
    ///     </para>
    ///     <para>
    ///     For the full standalone dashboard at a configurable route, use
    ///     <see cref="AddStyloBotDashboard(IServiceCollection, Action{StyloBotDashboardOptions}?)"/> instead.
    ///     </para>
    /// </summary>
    /// <example>
    ///     Lightweight setup (tag helpers only):
    ///     <code>
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddStyloBotUI();
    ///     // Now use &lt;sb-badge /&gt;, &lt;sb-gate&gt;, &lt;sb-human&gt; etc. in your Razor views
    ///     </code>
    /// </example>
    public static IServiceCollection AddStyloBotUI(this IServiceCollection services)
    {
        services.AddHttpContextAccessor(); // Needed by sb-* gating TagHelpers

        // Detection data extraction for ViewComponents and TagHelpers.
        // Uses a factory so DI does not attempt to resolve IHttpClientFactory unless it is registered.
        services.TryAddSingleton<DetectionDataExtractor>(sp =>
        {
            var factory = sp.GetService<IHttpClientFactory>();
            var options = sp.GetService<IOptions<DetectionApiOptions>>();
            return new DetectionDataExtractor(factory, options);
        });

        return services;
    }

    /// <summary>
    ///     Enables API-mode detection data extraction.
    ///     When neither inline middleware (HttpContext.Items) nor YARP headers provide detection data,
    ///     <see cref="DetectionDataExtractor"/> will call <paramref name="apiEndpoint"/> to retrieve
    ///     detection results for the current visitor.
    ///     <para>
    ///     The result is cached in <c>HttpContext.Items</c> by <see cref="SbDetectionMiddleware"/> so
    ///     synchronous <c>sb-*</c> tag helpers always see the data without making async calls.
    ///     </para>
    ///     <para>
    ///     After calling this, add <see cref="SbDetectionMiddleware"/> to the pipeline:
    ///     <code>app.UseMiddleware&lt;SbDetectionMiddleware&gt;();</code>
    ///     </para>
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="apiEndpoint">
    ///     URL of the StyloBot API detection endpoint,
    ///     e.g. <c>"http://gateway:8080/api/v1/me"</c>.
    /// </param>
    /// <example>
    ///     <code>
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddStyloBotUI();
    ///     builder.Services.AddStyloBotApiMode("http://gateway:8080/api/v1/me");
    ///     // ...
    ///     app.UseMiddleware&lt;SbDetectionMiddleware&gt;();
    ///     </code>
    /// </example>
    public static IServiceCollection AddStyloBotApiMode(
        this IServiceCollection services,
        string apiEndpoint)
    {
        services.Configure<DetectionApiOptions>(o => o.Endpoint = apiEndpoint);

        // Register the named HTTP client used for API calls. Ships the
        // StyloBot.Internal UA so the detection pipeline classifies traffic
        // from this host as a known-internal client (not "Wget" because
        // the default .NET HttpClient sends no UA at all).
        services.AddHttpClient("stylobot", c =>
            c.DefaultRequestHeaders.UserAgent.ParseAdd(StyloBotInternalUserAgent.Value));

        // DetectionDataExtractor auto-detects API mode via optional IOptions<DetectionApiOptions>.
        // If AddStyloBotUI has already registered it, remove and re-register so the DI container
        // resolves the constructor with IHttpClientFactory + IOptions<DetectionApiOptions>.
        services.RemoveAll<DetectionDataExtractor>();
        services.AddSingleton<DetectionDataExtractor>();

        return services;
    }

    /// <summary>
    ///     Full StyloBot setup: detection + dashboard services.
    ///     This is the recommended entry point for most applications.
    ///     Pair with <see cref="UseStyloBot"/> in the middleware pipeline.
    /// </summary>
    /// <example>
    ///     <code>
    ///     builder.Services.AddStyloBot(dashboard => {
    ///         dashboard.AllowUnauthenticatedAccess = true; // dev only
    ///     });
    ///     app.UseRouting();
    ///     app.UseStyloBot();
    ///     app.MapControllers();
    ///     </code>
    /// </example>
    public static IServiceCollection AddStyloBot(
        this IServiceCollection services,
        Action<StyloBotDashboardOptions>? configureDashboard = null,
        Action<BotDetectionOptions>? configureDetection = null)
    {
        services.AddBotDetection(configureDetection);
        services.AddStyloBotDashboard(configureDashboard);
        return services;
    }

    /// <summary>
    ///     Adds Stylobot Dashboard services to the service collection.
    ///     For most applications, use <see cref="AddStyloBot"/> instead which includes detection.
    ///     <para>
    ///     Internally calls <see cref="AddStyloBotUI"/> so all tag helpers are available too.
    ///     </para>
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configure">Configuration options</param>
    /// <returns>The service collection for chaining</returns>
    /// <summary>
    ///     Adds Stylobot Dashboard services, binding options from <c>StyloBot:Dashboard</c>
    ///     in <paramref name="configuration"/> before applying the optional <paramref name="configure"/> lambda.
    ///     FOSS users can set <c>MonitoringPack:Enabled</c> in appsettings.json without a code change.
    /// </summary>
    public static IServiceCollection AddStyloBotDashboard(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<StyloBotDashboardOptions>? configure = null)
    {
        return services.AddStyloBotDashboard(options =>
        {
            configuration.GetSection("StyloBot:Dashboard").Bind(options);
            configure?.Invoke(options);
        });
    }

    public static IServiceCollection AddStyloBotDashboard(
        this IServiceCollection services,
        Action<StyloBotDashboardOptions>? configure = null)
    {
        var options = new StyloBotDashboardOptions();
        configure?.Invoke(options);

        services.AddSingleton(options);
        // Also expose via IOptions<> so ViewComponents that DI IOptions<StyloBotDashboardOptions>
        // (SbTopBotsViewComponent, SbSessionsListViewComponent, etc.) see the configured instance
        // instead of a freshly-constructed default. Without this the host's options.BasePath
        // override is silently ignored on every IOptions<>-injected widget -- links emit
        // /stylobot/signature/... using the FOSS default while the middleware-rendered partials
        // correctly emit /dashboard/signature/..., producing inconsistent navigation.
        services.AddSingleton<IOptions<StyloBotDashboardOptions>>(MEOptions.Create(options));

        // Register lightweight UI services (tag helpers, view components)
        services.AddStyloBotUI();

        // F2 — dashboard IA contribution registry. Packs (ASP.NET Pack, OTel
        // Mesh, etc.) register IDashboardContribution singletons via
        // AddDashboardContribution<T>(); the registry composes them by slot
        // and the aggregate Razor views invoke the named ViewComponent per
        // contribution. Packs that aren't loaded contribute nothing
        // (feedback_remote_mode_optional_di).
        services.AddSingleton<DashboardContributionRegistry>();

        services.AddSignalR();

        // Memory cache - used by StyloBotDashboardMiddleware widget render cache (2s TTL per widget)
        services.AddMemoryCache();

        // Ensure MVC services are available for Razor view rendering (idempotent)
        services.AddControllersWithViews();

        // Razor view renderer for middleware-hosted dashboard
        services.AddSingleton<RazorViewRenderer>();

        // Liquid template renderer for Node SDK widget rendering
        services.AddSingleton<LiquidWidgetRenderer>();

        // Dashboard help system (Markdig-rendered markdown)
        services.AddSingleton<DashboardHelpService>();

        // Signal vocabulary catalogue. Backs /dashboard/signals/* autocomplete
        // for the Policy Stack expression editor. Reflects SignalKeys constants
        // + XML doc summaries + embedded VYaml overlays; immutable post-load,
        // so a blocking GetAwaiter().GetResult() at boot is intentional.
        //
        // Phase F: any ISignalCatalogSource registrations (e.g. PrometheusPack's
        // MeterSignalCatalogSource) are passed through so dynamic vocabularies
        // -- live meter names, in particular -- appear in the editor catalog
        // alongside the static SignalKeys constants. The catalog enumerates
        // them lazily on read; the snapshot returned by GetAwaiter().GetResult()
        // here just captures the source set, not the source contents.
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Signals.ISignalCatalog>(sp =>
        {
            var asm = typeof(Mostlylucid.BotDetection.Models.SignalKeys).Assembly;
            var sources = sp.GetServices<Mostlylucid.BotDetection.Policies.Signals.ISignalCatalogSource>();

            // Build the signal_key -> detector_name(s) inverted index from any
            // detector manifests the host already registered. The manifests are
            // the source-of-truth that every contributor pack declares anyway,
            // so we get the dashboard's "Source" column provenance for free
            // without a curated string list in C# (per feedback_no_word_lists).
            // When no manifest loader is registered (some test hosts), the
            // catalog falls back to an empty index and the view component
            // shows the SignalKeys DeclaringType.
            var manifestLoader = sp.GetService<Mostlylucid.BotDetection.Orchestration.Manifests.DetectorManifestLoader>();
            var emittedBy = manifestLoader is null
                ? null
                : Mostlylucid.BotDetection.Policies.Signals.SignalEmissionIndex.Build(manifestLoader);

#pragma warning disable IL2026 // SignalCatalog.LoadAsync reflects const fields; overlays are pre-registered for AOT.
            return Mostlylucid.BotDetection.Policies.Signals.SignalCatalog
                .LoadAsync(asm, sources, emittedBy).GetAwaiter().GetResult();
#pragma warning restore IL2026
        });

        // Policy Stack control read surface (FOSS default). Commercial Postgres
        // pack overrides IPolicyRuleStore / IPolicyDecisionLog via TryAdd-loses
        // wiring; these registrations only land if no commercial pack provided
        // one first. The YamlPolicyRuleStore InitializeAsync() pulls embedded
        // seed YAML synchronously at boot -- the corpus is fixed for the
        // process lifetime, so blocking on .GetResult() is intentional.
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Rules.IPolicyRuleStore>(_ =>
        {
            var asm = typeof(Mostlylucid.BotDetection.Policies.Rules.PolicyRule).Assembly;
            var store = Mostlylucid.BotDetection.Policies.Rules.YamlPolicyRuleStore.FromEmbeddedResources(
                asm,
                "Mostlylucid.BotDetection.Policies.Rules.SeedRules.");
            store.InitializeAsync().GetAwaiter().GetResult();
            return store;
        });
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Resolution.IPolicyResolver,
            Mostlylucid.BotDetection.Policies.Resolution.DefaultPolicyResolver>();
        // Phase G runtime: the armed-rule registry is the single source of
        // truth for "which trigger rules are currently armed". Register on
        // every host -- viewer-only processes won't have a MeterTriggerService
        // populating it (which is fine; CurrentlyArmedAsync returns empty),
        // but the resolver constructor needs a non-null reference to merge
        // the (empty) set.
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Triggers.ArmedRuleRegistry>();
        // Throttle pipeline enforcement: shared ITokenBucketStore keyed by
        // the throttle rule's composite PolicyScope key. The single primitive
        // is shared with RateLimit (Wave 3 consolidation, see
        // feedback_no_duplication). The default in-memory implementation is
        // registered by AddBotDetection -- this TryAddSingleton is a safety
        // net for dashboard hosts that skip the FOSS bot-detection wiring.
        services.TryAddSingleton<Mostlylucid.BotDetection.RateLimit.ITokenBucketStore,
            Mostlylucid.BotDetection.RateLimit.InMemoryTokenBucketStore>();
        // FOSS default: in-process log. Commercial SQLite / Postgres impls slot
        // in via TryAdd from their respective packs. Operators that want SQLite
        // durability on FOSS can replace this registration explicitly.
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Decisions.IPolicyDecisionLog,
            Mostlylucid.BotDetection.Policies.Decisions.InMemoryPolicyDecisionLog>();
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Telemetry.IPolicyEffectivenessCache,
            Mostlylucid.BotDetection.Policies.Telemetry.PolicyEffectivenessCache>();
        // Hosted-service lifecycle: start the cache drainer. Cast through the
        // interface so the singleton resolution wins -- the PolicyEffectivenessCache
        // singleton needs to be the SAME instance the hosted service uses.
        services.AddHostedService<Mostlylucid.BotDetection.UI.Services.PolicyEffectivenessCacheHostedService>();

        // SbPolicyStack view-component presenter. Pure read surface; stateless.
        // The conflict analyzer is a peer singleton -- the presenter only runs
        // it when the Stack tab is the active surface, so DI cost is one
        // allocation regardless of how many call sites embed the control.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyConflictAnalyzer>();
        // Explainer presenter is registered before the stack presenter so the
        // stack presenter's optional constructor parameter resolves to the
        // real explainer rather than null. The explainer is a pure read
        // surface, stateless, peer-singleton with the stack presenter.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyExplainerPresenter>();
        // T4 Part B -- rule-divergence probe. The FOSS default reads
        // YamlPolicyRuleStore.DivergedRuleIds (in-memory only, wiped on
        // restart). The commercial Postgres overlay replaces this binding
        // with a probe that consults policy_rules.override_diverged
        // (durable across restarts). Registered before the presenter so
        // the presenter's optional ctor parameter resolves cleanly.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.IRuleDivergenceProbe,
                                 Mostlylucid.BotDetection.UI.Services.YamlRuleDivergenceProbe>();
        // A10 -- per-group Owned / Effective resolver. Pure function of
        // (scope, allRules); no state, no I/O. Registered before the
        // presenter so the presenter's optional constructor parameter
        // resolves to the real resolver rather than its self-constructed
        // fallback instance.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.IEffectiveStackResolver,
                                 Mostlylucid.BotDetection.UI.Services.EffectiveStackResolver>();
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyStackPresenter>();
        // C6 expression-editor presenter. Pure read surface; never mutates a
        // rule. The actual write goes through the commercial mutation API
        // (/api/v1/policies, C3); this presenter just shapes the existing
        // rule (or empty defaults) into the edit-row view model.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyEditPresenter>();
        // Editor debounce timings carried to the JS via data-* attributes on
        // the edit-row article. Defaults match the historical FOSS literals
        // (80ms parse, 500ms backtest); commercial widens these via
        // Configure<PolicyEditTimingsOptions> backed by PolicyStackEditorOptions
        // so an operator override on the wider class flows through to the JS.
        services.AddOptions<Mostlylucid.BotDetection.UI.Configuration.PolicyEditTimingsOptions>();

        // Task 18: policy-stack edit gate. SbPolicyStackViewComponent reads
        // this to decide whether to render edit affordances (pencil, drag
        // handle, + Add rule, Promote / Cancel auto-promote). The FOSS
        // default returns false always so the dashboard ships read-only by
        // construction; the commercial overlay registers a license- and
        // role-aware implementation. TryAddSingleton so a commercial host
        // that registers its own binding before AddStyloBotDashboard keeps
        // its instance.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.IPolicyCanEditPolicy,
            Mostlylucid.BotDetection.UI.Services.AlwaysReadOnlyPolicyCanEditPolicy>();

        // Pack Metrics B1 -- /dashboard/insights page composer. Pure read; goes
        // through IMeterStream only (no DB). Stateless, peer-singleton with the
        // other dashboard presenters above. The IMeterStream binding itself is
        // owned by PrometheusPack (AddPrometheusPack registers Local or Remote).
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.InsightsPageBuilder>();

        // Pack Metrics B8 -- policy-stack summary endpoint composer. Pure read;
        // both dependencies (IPolicyRuleStore, IPolicyDecisionLog) are optional
        // per feedback_remote_mode_optional_di: rule store null -> endpoint 404,
        // decision log null -> DecisionsLast15m omitted. Stateless singleton
        // peer of the other presenters; the middleware route resolves it via
        // RequestServices on each call.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyStackSummaryBuilder>();

        // Single chokepoint for "what URL should a dashboard link to". Reads
        // StyloBotDashboardOptions.BasePath (or NavBasePath when set) and
        // prefixes every subpath the health-summary providers + pack-row
        // links + signature/entity card hrefs need. Replaces a sweep of
        // hard-coded "/dashboard/..." strings that 404'd anywhere the
        // middleware was mounted at a non-default path (e.g. the ASP.NET
        // Trailblazor demo's /_stylobot mount).
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.IDashboardLinkResolver,
            Mostlylucid.BotDetection.UI.Services.DashboardLinkResolver>();

        // Pack Metrics C1 -- dashboard overview pack-health row. The three FOSS
        // providers ship here; each one wraps an existing summary builder in
        // process (no HTTP loopback into the gateway's own JSON endpoints). The
        // commercial overlay registers additional providers via the same
        // IPackHealthSummaryProvider interface so its packs slot in alongside.
        // Ordering: Policy (100, leftmost) -- AspNet (200) -- Metrics (300).
        services.AddSingleton<Mostlylucid.BotDetection.UI.Services.IPackHealthSummaryProvider,
            Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders.PolicyStackHealthSummaryProvider>();
        services.AddSingleton<Mostlylucid.BotDetection.UI.Services.IPackHealthSummaryProvider,
            Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders.AspNetPackHealthSummaryProvider>();
        // Factory variant: IMeterStream is only registered when a host calls
        // AddPrometheusPack. A Demo / dev-bench host won't have it, but the
        // dashboard wiring registers this provider unconditionally. Resolving
        // through the factory lets the provider's null path return a null tile
        // (the row builder skips it) instead of failing ValidateOnBuild.
        services.AddSingleton<Mostlylucid.BotDetection.UI.Services.IPackHealthSummaryProvider>(sp =>
            new Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders.MeterStreamHealthSummaryProvider(
                sp.GetService<Mostlylucid.BotDetection.PrometheusPack.Telemetry.IMeterStream>(),
                sp.GetService<Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders.MeterStreamHealthTileCache>(),
                sp.GetService<Mostlylucid.BotDetection.UI.Services.IDashboardLinkResolver>()));
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.DashboardOverviewPackHealthRowBuilder>();

        // Plan 3 Task 1 -- per-surface tick/version cursor. ONE monotonic
        // counter shared by all dashboard producers; consumers check
        // TickFor(surface) to skip already-fresh widgets. Tick advanced on
        // Tick10s via DashboardChangeCursorTickHook (IHostedService shim,
        // NOT a BackgroundService per feedback_no_background_services).
        // TryAdd so a commercial host that replaces the cursor keeps its impl.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.IDashboardChangeCursor,
            Mostlylucid.BotDetection.UI.Services.DashboardChangeCursor>();
        services.AddHostedService<Mostlylucid.BotDetection.UI.Services.DashboardChangeCursorTickHook>();

        // Plan 3 Task 2 -- wire cursor into SignalRBroadcastConstrainer so the
        // BroadcastDirty beacon carries the correct CurrentTick. The cursor is a
        // singleton; setting it once on the static constrainer is safe for the
        // process lifetime. The setter is idempotent so repeated AddStyloBotDashboard
        // calls (multi-pack hosts) don't corrupt the static slot.
        services.AddHostedService<Mostlylucid.BotDetection.UI.Services.BroadcastConstrainerCursorWire>();

        // #122 -- Centralised dashboard freshness beacon. ONE invalidation
        // channel for every pack-health summary tile (policy stack, AspNet
        // pack, meter stream). Producers publish a surface key; consumers
        // subscribe via SignalR + HTMX OOB swap; the rendered tile carries
        // data-sb-widget / data-sb-depends derived from
        // StatTileViewModel.BeaconKey. Per feedback_centralised_change_detection:
        // dashboard caches MUST share ONE mechanism, not private per-builder
        // TTLs / warmups / invalidation logic.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.DashboardFreshnessBeacon>();
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyStackSummaryCache>();
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders.MeterStreamHealthTileCache>();
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders.AspNetPackHubTileCache>();

        // Site-health widget: sampler subscribes to Tick10s and persists
        // DegradationAtom snapshots via IDashboardEventStore. The view
        // component reads the same store directly (no parallel
        // ISiteHealthQuery indirection any more). Per
        // [[feedback_no_inmemory_stores]] this replaces the deleted
        // DegradationHistoryAtom ring that lost the window on every
        // restart. DegradationAtom may be absent on hosts that pared the
        // rate-limit feature out entirely; the sampler ctor null-checks
        // via DI optional-resolve below.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.DegradationStoreSampler>();
        services.AddHostedService<Mostlylucid.BotDetection.UI.Services.DegradationStoreSamplerBootstrap>();
        // Bridge: subscribes IPolicyRuleStore.Changed -> beacon, and
        // IScheduleCoordinator Tick10s -> beacon (when the meter catalog
        // size changes). Each upstream is optional per
        // feedback_remote_mode_optional_di; absent inputs self-disable
        // that producer arm.
        services.AddHostedService<Mostlylucid.BotDetection.UI.Services.DashboardFreshnessBridge>();

        // B6 -- Policy Stack live-update beacon. SignalR by default; commercial
        // packs replace this with a Redis-fanned implementation so an edit on
        // one node reaches every other node's connected browsers. The hosted
        // service bridges the rule store's Changed event into the broadcaster
        // and stays running for the host lifetime.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Policies.IPolicyChangeBroadcaster,
            Mostlylucid.BotDetection.UI.Policies.SignalRPolicyChangeBroadcaster>();
        services.AddHostedService<Mostlylucid.BotDetection.UI.Policies.PolicyChangeNotificationHostedService>();

        // HR2 -- fingerprint name-slot dirty beacon. Dashboard hosts (this extension's
        // callers) get the SignalR-backed broadcaster that fans the beacon out to every
        // connected operator browser via IStyloBotDashboardHub.FingerprintDirty. The
        // FOSS core registers a NoOp default; Replace here so the commercial editor +
        // HR1 Redis subscriber resolve the live SignalR impl on dashboards while
        // lightweight viewer hosts still get the safe no-op.
        services.Replace(
            ServiceDescriptor.Singleton<
                Mostlylucid.BotDetection.Identity.IFingerprintDirtyBroadcaster,
                Mostlylucid.BotDetection.UI.Hubs.SignalRFingerprintDirtyBroadcaster>());

        // Dashboard tooltip registry — loads Definitions/Tooltips/*.yaml at
        // startup. Cheap to register unconditionally; the helper short-circuits
        // when StyloBotDashboardOptions.EnableTooltips is false so the resolved
        // registry never gets queried on FOSS hosts that haven't opted in.
        services.AddSingleton<DashboardTooltipRegistry>();

        // Curated facet picker catalog — loads the ~25 webmaster-facing facets
        // (label / value-type / per-facet operator subset) from FOSS-embedded
        // YAML at Definitions/PolicyFacets/picker-catalog.yaml. The inline
        // policy rule editor (B8) reads this for its facet dropdown; the raw
        // 380+ SignalKeys remain accessible via the text DSL on expand.
        services.AddSingleton<IFacetPickerCatalog, FacetPickerCatalog>();
        services.TryAddSingleton<FacetPillRenderer>();

        // Datalist-backed autocomplete for the few string facets whose value
        // space is owned by a FOSS catalog (e.g. verifiedbot.name). Reads
        // BotPatternLoader / WellKnownBotIndex at construction time. Plain
        // string facets without an authoritative source (request.domain,
        // ip.asn_org) deliberately have no entry here -- the picker falls
        // back to a free-text input rather than fabricating options.
        services.TryAddSingleton<IFacetAutocompleteSource, FacetAutocompleteSource>();

        // Bounded, scope-keyed counter of policy rule hits per intent. Atom-backed
        // (in-memory), not persisted -- the tick.1m subscription below drains
        // records older than RetentionWindow so memory stays bounded without a
        // background polling loop.
        // Dashboard IA collapse (F1). Bound from Dashboard:Layout. V2Enabled is the
        // kill-switch for the new Traffic / Visitors / Site IA; defaults to false so
        // the legacy 10+ tab sidebar continues until M1 flips it on. URL ?legacy=1
        // forces legacy regardless. Removed in the last migration step.
        services.AddOptions<DashboardLayoutOptions>()
            .BindConfiguration("Dashboard:Layout");

        // Threats card (Traffic page) inclusion thresholds. The card was
        // hiding 11k+ classified-bot rows behind a strict "ThreatBand >=
        // Medium" filter that, post-#178, only matched Internal pings. The
        // widened filter adds a BotProbability floor as an OR clause; this
        // section makes the floor + top-N tunable per
        // feedback_all_settings_configurable.
        services.AddOptions<ThreatsOptions>()
            .BindConfiguration(ThreatsOptions.SectionName);

        services.AddOptions<Mostlylucid.BotDetection.UI.Options.PolicyStackHitAtomOptions>()
            .BindConfiguration("BotDetection:Policies:HitAtom");
        // Atom ctor needs TimeProvider; AddStyloBotBotDetection registers it but
        // dashboard-only hosts (test fixtures, marketing-site embed) don't always
        // call that extension. TryAdd preserves any upstream registration.
        services.TryAddSingleton<TimeProvider>(TimeProvider.System);
        // PolicyStackPostureClassifier (C14) needs PolicyIntentClassifier (C2);
        // C2 registers it via AddPolicyDispatcher, but the marketing-site
        // dashboard host calls only AddStyloBotDashboard. Without this TryAdd,
        // /dashboard/policies 500s on PostureViewComponent activation.
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Rules.PolicyIntentClassifier>();
        services.TryAddSingleton<PolicyStackHitAtom>();
        // Real ScheduleCoordinator + IHostedService triplet. Historically this
        // block registered NullScheduleCoordinator.Instance as a defensive
        // default so PolicyStackHitAtomTickHook's ctor injection wouldn't tear
        // down dashboard-only hosts that hadn't called AddBotDetection. The
        // problem: AddPrometheusPack.EnsureScheduleCoordinatorRegistered then
        // saw "something already there, skip", and the null sentinel silently
        // shadowed the real one -- RemoteMeterStream's Subscribe(...) calls
        // landed on a no-op coordinator, Tick10s never fired, and the AspNet
        // pack metrics tile rendered "0 meters" on every viewer host. TryAdd
        // here lets AddBotDetection's earlier registration win when it's
        // present (it registers the same triplet); when it isn't, we still
        // get a real coordinator running.
        services.AddOptions<Mostlylucid.BotDetection.Scheduling.ScheduleCoordinatorOptions>()
            .BindConfiguration(Mostlylucid.BotDetection.Scheduling.ScheduleCoordinatorOptions.SectionName);
        services.TryAddSingleton<Mostlylucid.BotDetection.Scheduling.ScheduleCoordinator>();
        services.TryAddSingleton<Mostlylucid.Common.Scheduling.IScheduleCoordinator>(
            sp => sp.GetRequiredService<Mostlylucid.BotDetection.Scheduling.ScheduleCoordinator>());
        services.AddHostedService(
            sp => sp.GetRequiredService<Mostlylucid.BotDetection.Scheduling.ScheduleCoordinator>());
        services.AddHostedService<PolicyStackHitAtomTickHook>();

        // Replace the FOSS NullPolicyHitRecorder (registered by AddPolicyDispatcher)
        // with the atom-backed implementation so the policy dispatcher's hot-path
        // Record(...) calls land in the same bucket the posture view component
        // reads. RemoveAll+AddSingleton (instead of TryAddSingleton) because
        // TryAdd is a no-op when the null default is already registered; we
        // explicitly want the atom to win when the dashboard pack is present.
        services.RemoveAll<Mostlylucid.BotDetection.Policies.Telemetry.IPolicyHitRecorder>();
        services.AddSingleton<Mostlylucid.BotDetection.Policies.Telemetry.IPolicyHitRecorder>(
            sp => sp.GetRequiredService<PolicyStackHitAtom>());

        // Derives a 4-level posture (Permissive / Balanced / Strict / Lockdown)
        // from the live rule set + a PolicyStackHitSnapshot from the atom above,
        // plus a single suggestion arm. Pure compute; no DB, no per-request work.
        services.AddOptions<Mostlylucid.BotDetection.UI.Options.PostureClassifierOptions>()
            .BindConfiguration("BotDetection:Policies:Posture");
        services.TryAddSingleton<PolicyStackPostureClassifier>();

        // Static detection-side data the dashboard renders need. Registered as
        // TryAddSingleton so that hosts which also call AddBotDetection get
        // those richer registrations instead. Pure dashboard-viewer hosts
        // (header-driven, no detection pipeline, no DB) get just these stubs:
        //
        //   - IdentityVectorLayout: the slot map (static, embedded resources).
        //   - IdentityVectorEncoder: stateless wrapper around the layout.
        //   - IdentityArchetypeRegistry: archetype dictionary loaded from
        //     YAML embedded in Mostlylucid.BotDetection.
        //
        // None of these touch a database or run the detection pipeline.
        services.TryAddSingleton(sp => IdentityVectorLayout.DefaultV1());
        services.TryAddSingleton<IdentityVectorEncoder>();
        services.TryAddSingleton<IdentityArchetypeRegistry>();

        // Dashboard event store: SQLite for FOSS (persists across restarts).
        // Commercial PostgreSQL package overrides via TryAddSingleton.
        services.TryAddSingleton<IDashboardEventStore, SqliteDashboardEventStore>();

        // Widget catalog: reflection scan over loaded assemblies at first use.
        // Singleton — the catalog is immutable after boot.
        services.AddSingleton(_ =>
            Mostlylucid.BotDetection.UI.Dashboard.Composition.DashboardWidgetCatalog.BuildFromLoadedAssemblies());

        // Per-request page composer: maps a DashboardPageManifest → one ComposeBatchAsync.
        // Scoped so it captures the per-request IDashboardEventStore if one is registered
        // as scoped (Postgres override path); for the FOSS singleton SQLite store, scoped
        // resolves cleanly from the singleton scope.
        services.AddScoped<Mostlylucid.BotDetection.UI.Dashboard.Composition.IDashboardPageComposer,
            Mostlylucid.BotDetection.UI.Dashboard.Composition.DefaultDashboardPageComposer>();

        // Out-of-request content cache: gateway-owned, fresh-by-tick, over the canonical
        // SlidingCacheAtom. The request path reads through it (cache-first) and the tick
        // materializer warms hot pages into it. Registered as a SINGLETON (shared across
        // requests + the materializer) — so its compose factory resolves the SCOPED
        // composer through a fresh scope per compose (a singleton can't capture a scoped
        // dependency). The current tick comes from the change cursor.
        services.AddOptions<Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardMaterializerOptions>();
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Dashboard.Materialization.IDashboardContentCache>(sp =>
        {
            var scopeFactory = sp.GetRequiredService<IServiceScopeFactory>();
            var cursor = sp.GetRequiredService<Mostlylucid.BotDetection.UI.Services.IDashboardChangeCursor>();
            var options = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<
                Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardMaterializerOptions>>();
            return new Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardContentCache(
                compose: async (manifest, window, ct) =>
                {
                    using var scope = scopeFactory.CreateScope();
                    var composer = scope.ServiceProvider
                        .GetRequiredService<Mostlylucid.BotDetection.UI.Dashboard.Composition.IDashboardPageComposer>();
                    return await composer.ComposeAsync(manifest, window, ct);
                },
                currentTick: () => cursor.CurrentTick,
                options: options);
        });

        // Tick-driven materializer (Dashboard Coordinator batch side): each Tick10s it
        // warms the content cache's live envelopes so the request path reads a ready
        // bundle instead of composing. Self-disables on a viewer-mode host with no
        // IScheduleCoordinator (its coordinator dep is optional).
        services.AddHostedService<Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardMaterializerCoordinator>();

        // Page manifest source: empty by default; Task 3 adds the traffic manifest.
        // TryAdd so a commercial host that seeds its own manifests keeps them.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Dashboard.Composition.IDashboardPageManifestSource,
            Mostlylucid.BotDetection.UI.Dashboard.Composition.DefaultDashboardPageManifestSource>();

        // Operator-supplied signature labels (for detector weighting / ground truth).
        // In-memory by default; production hosts can register SQLite / PostgreSQL impls.
        services.TryAddSingleton<ISignatureLabelStore, SqliteSignatureLabelStore>();

        // Aggregate cache - populated by beacon, read by API endpoints
        services.AddSingleton<DashboardAggregateCache>();

        // Write-through signature cache - single source of truth for top bots
        services.AddSingleton<SignatureAggregateCache>();

        // Stateless UA aggregator - used by broadcaster beacon + view components with params
        services.TryAddSingleton<DashboardUserAgentAggregator>();

        // Background beacon - computes all dashboard aggregates periodically.
        // Wave 2 migrated: subscribes to ScheduleCoordinator.Tick10s at
        // construction and gates the compute pass on the configured interval.
        // UiHostedSingletonsBootstrap eagerly resolves it at boot so the
        // subscription is live before the first tick.
        services.AddSingleton<DashboardSummaryBroadcaster>();
        services.AddHostedService<UiHostedSingletonsBootstrap>();

        // BDF export service. Also registered in AddStyloBotApi for hosts that call
        // AddStyloBotApi but not AddStyloBotDashboard (the gateway); TryAdd is safe.
        services.TryAddSingleton<BdfExportService>();

        // BDF harvest debug surface: when BotDetection:Debug:BdfHarvest:Enabled = true,
        // /api/v1/bdf/harvest streams persisted BDFs as NDJSON for offline archetype-YAML
        // authoring via the `stylobot archetype-from-bdf` console command. Off by default.
        services.AddOptions<BdfHarvestOptions>().BindConfiguration("BotDetection:Debug:BdfHarvest");
        services.TryAddSingleton<BdfHarvestService>();

        // Periodicity heatmap aggregator - per-signature 7x24 day/hour grid for the
        // signature detail page, aggregated on-demand from the requests table
        // (no new schema). Renders "when does this actor hit me" at a glance.
        services.AddSingleton<SignaturePeriodicityHeatmap>();

        // Pinned endpoint store - persists operator-added paths to SQLite
        services.TryAddSingleton<IPinnedEndpointStore, SqlitePinnedEndpointStore>();

        // Warm the SignatureAggregateCache from DB on startup so Top Bots / Live Activity /
        // visitor list aren't empty after restart. Cache is in-memory read-through;
        // persistence is the source of truth in IDashboardEventStore.
        //
        // Gateway-only per project_gateway_data_locality. On remote-mode hosts
        // (marketing site, stylobot-ui) the warmup would issue DB queries against
        // a store that doesn't belong to this process AND freeze the cache at a
        // stale startup snapshot — DetectionBroadcastMiddleware runs only on the
        // gateway, so nothing refreshes the cache afterwards. That produces the
        // classic "home YOU card shows 100% / signature detail shows 7% for the
        // same sig" divergence: home reads cache (stale), detail reads event
        // store (current). Skip the warmup when AddStyloBotDashboardRemote has
        // already registered DashboardSourceOptions; the cache stays registered
        // (satisfies DI for middleware / view-components) but stays empty, so
        // all read paths cleanly fall through to IDashboardEventStore — which
        // on remote-mode hosts is RemoteDashboardEventStore (REST → gateway).
        // One source of truth.
        if (!services.Any(s => s.ServiceType == typeof(Mostlylucid.BotDetection.UI.Adapters.Remote.DashboardSourceOptions)))
        {
            services.AddHostedService<SignatureAggregateCacheWarmupService>();
        }

        // Route catalog: discovery + manual names + OpenAPI cross-reference. FOSS feature,
        // shared by the dashboard Routes tab and (future) auto-honeypot generation.
        services.TryAddSingleton<IRouteDiscoveryService>(sp =>
            new RouteDiscoveryService(sp.GetServices<EndpointDataSource>()));

        services.TryAddSingleton<IRouteNameStore>(sp =>
        {
            var botOpts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
            var connStr = DashboardDbPath.GetConnectionString(botOpts);
            var logger = sp.GetRequiredService<ILogger<SqliteRouteNameStore>>();
            return new SqliteRouteNameStore(connStr, logger);
        });

        services.TryAddSingleton<IOpenApiCatalog, OpenApiCatalog>();
        services.AddHttpClient("stylobot-openapi", c =>
            c.DefaultRequestHeaders.UserAgent.ParseAdd(StyloBotInternalUserAgent.Value));
        services.TryAddSingleton<IOpenApiDocumentLoader, OpenApiDocumentLoader>();
        // Bind from the StyloBotDashboardOptions.OpenApi instance the user already configured.
        services.AddSingleton<IOptions<OpenApiSeedOptions>>(sp =>
            MEOptions.Create(sp.GetRequiredService<StyloBotDashboardOptions>().OpenApi));
        services.AddHostedService<OpenApiStartupSeederService>();
        services.AddHostedService<RouteNameStoreInitializer>();

        services.TryAddSingleton<IRouteCatalogService, RouteCatalogService>();

        // MonitoringPack
        if (options.MonitoringPack.Enabled)
        {
            services.TryAddSingleton<IMetricSnapshotStore>(sp =>
            {
                var botOpts = sp.GetRequiredService<IOptions<BotDetectionOptions>>().Value;
                var connStr = DashboardDbPath.GetConnectionString(botOpts);
                var logger = sp.GetRequiredService<ILogger<SqliteMetricSnapshotStore>>();
                return new SqliteMetricSnapshotStore(connStr, logger);
            });

            // Default no-op runtime controller. Commercial pack assemblies replace this
            // (via Replace, not TryAdd) to enable per-pack license-gated hot-reload.
            services.TryAddSingleton<IPackRuntimeController, NullPackRuntimeController>();

            if (options.MonitoringPack.Mode == MonitoringMode.Local)
            {
                services.AddSingleton<IMonitoringPack>(
                    new AspNetMonitoringPack(options.MonitoringPack.IncludeAspNetHostMeters));
                // Wave 2: MeterListenerService migrated to ScheduleCoordinator
                // tick.1m. Singleton + the dashboard bootstrap shim eagerly
                // resolves it so StartListening() runs and the tick
                // subscription is established at boot.
                services.AddSingleton<MeterListenerService>(sp =>
                    new MeterListenerService(
                        sp.GetServices<IMonitoringPack>(),
                        sp.GetRequiredService<IMetricSnapshotStore>(),
                        sp.GetRequiredService<ILogger<MeterListenerService>>(),
                        sp.GetRequiredService<IPackRuntimeController>(),
                        sp.GetService<Mostlylucid.Common.Scheduling.IScheduleCoordinator>()));
            }
            else if (options.MonitoringPack.Mode == MonitoringMode.GatewayServer)
            {
                services.AddSingleton<IMonitoringPack>(
                    new AspNetMonitoringPack(options.MonitoringPack.IncludeAspNetHostMeters));
                // Wave 2: GatewayMeterAccumulator no longer inherits from
                // BackgroundService. It has no periodic body -- StartListening
                // runs in the constructor and the remote dashboard host pulls
                // GetCurrentSnapshot() on its own polling cadence. The
                // BotDetection bootstrap shim drives eager resolution at boot.
                services.AddSingleton<GatewayMeterAccumulator>(sp =>
                    new GatewayMeterAccumulator(
                        sp.GetServices<IMonitoringPack>(),
                        sp.GetRequiredService<ILogger<GatewayMeterAccumulator>>()));
            }
            else if (options.MonitoringPack.Mode == MonitoringMode.RemoteClient
                     && options.MonitoringPack.GatewayMetricsUrl != null)
            {
                services.AddHttpClient("sb-metrics", c =>
                    c.DefaultRequestHeaders.UserAgent.ParseAdd(StyloBotInternalUserAgent.Value));
                // Wave 2 migrated: subscribes to ScheduleCoordinator.Tick10s
                // at construction and gates the poll on the configured remote-
                // poll interval. UiHostedSingletonsBootstrap (registered above
                // alongside DashboardSummaryBroadcaster) eagerly resolves it at
                // boot so the subscription is live before the first tick.
                services.AddSingleton<RemoteMetricCollector>(sp =>
                    new RemoteMetricCollector(
                        sp.GetRequiredService<IHttpClientFactory>(),
                        options.MonitoringPack.GatewayMetricsUrl,
                        options.MonitoringPack.RemotePollInterval,
                        sp.GetRequiredService<IMetricSnapshotStore>(),
                        sp.GetRequiredService<ILogger<RemoteMetricCollector>>(),
                        sp.GetService<Mostlylucid.Common.Scheduling.IScheduleCoordinator>()));
            }
        }

        // Left-nav row registry. Composes FossDashboardGroups + any IDashboardPack
        // singletons that packs (commercial or otherwise) register alongside their
        // IMonitoringPack registration. Singleton because the row set is stable
        // for the process lifetime.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Dashboard.IDashboardRowRegistry,
            Mostlylucid.BotDetection.UI.Dashboard.DashboardRowRegistry>();

        // LLM result callback for background classification coordinator
        services.TryAddSingleton<ILlmResultCallback, LlmResultSignalRCallback>();

        // Cluster description callback for background LLM cluster naming
        services.TryAddSingleton<IClusterDescriptionCallback, ClusterDescriptionSignalRCallback>();

        // Built-in Identity bearer/cookie auth for the dashboard (FOSS tier).
        // Mounts register/login/refresh endpoints at {BasePath}/auth/*.
        // Commercial OIDC is a separate concern - use AuthorizationFilter instead.
        if (options.RequireAuthentication)
        {
            services.AddIdentityApiEndpoints<StyloBotUser>()
                .AddUserStore<StyloBotUserStore>()
                .AddDefaultTokenProviders();

            // Dev no-op sender: logs tokens to console. Override with AddStyloBotSmtp()
            // or register your own IEmailSender<StyloBotUser> after this call.
#pragma warning disable CS0618 // Kept for backward-compat DI lookups; new code uses Mostlylucid.Notify (AddNotifyEmailLogging).
            services.TryAddTransient<IEmailSender<StyloBotUser>, StyloBotDevEmailSender>();
#pragma warning restore CS0618
        }

        // Register dashboard data API paths with the bot detection policy system.
        // Detection runs on ALL paths including dashboard API - no exclusions.
        // BotDetectionMiddleware resolves the detection policy for these paths
        // and applies the configured action policy automatically.
        services.PostConfigure<BotDetectionOptions>(opts =>
        {
            var policyName = options.DataApiDetectionPolicy;
            var basePath = options.BasePath.TrimEnd('/');

            if (!opts.Policies.TryGetValue(policyName, out var policyConfig) || policyConfig == null)
            {
                policyConfig = new DetectionPolicyConfig();
                opts.Policies[policyName] = policyConfig;
            }

            if (string.IsNullOrWhiteSpace(policyConfig.ActionPolicyName))
                policyConfig.ActionPolicyName = options.DataApiActionPolicyName;
            policyConfig.ActionPolicyOverridable = true;

            opts.PathPolicies[$"{basePath}/api/**"] = policyName;
        });

        services.AddDashboardEndpointPerfBaseline();

        return services;
    }

    /// <summary>
    ///     Full StyloBot setup: detection + dashboard in the correct middleware order.
    ///     This is the recommended way to add StyloBot to your application.
    ///     <para>
    ///     Registers: detection middleware, broadcast middleware, dashboard UI, SignalR hub.
    ///     The broadcast middleware wraps detection so ALL detections (including blocked requests)
    ///     are recorded in the dashboard - no middleware ordering issues.
    ///     </para>
    /// </summary>
    /// <example>
    ///     <code>
    ///     builder.Services.AddStyloBot();             // or AddBotDetection() + AddStyloBotDashboard()
    ///     app.UseRouting();
    ///     app.UseStyloBot();                           // detection + dashboard, correct order guaranteed
    ///     app.MapControllers();
    ///     </code>
    /// </example>
    public static IApplicationBuilder UseStyloBot(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetService<StyloBotDashboardOptions>();

        // Anti-spoofing: drop X-Bot-Detection-* verdict headers a visitor attached
        // (this host computes its own and re-emits via UseStyloBotForwardedHeaders),
        // and — when ForwardedHeaders:StripInboundClientSignalHeaders is enabled —
        // client-signal headers (X-JA3-*, X-Client-TLS-*, …) that only a trusted
        // upstream proxy may inject. Must run before anything reads headers.
        app.UseStyloBotInboundClientHeaderStrip();

        if (options?.Enabled == true)
        {
            // Dashboard CSS/JS lives at /_content/Mostlylucid.BotDetection.UI/...
            // Ensure static files middleware is active so these assets are served.
            app.UseStaticFiles();

            // Broadcast middleware goes FIRST - it wraps detection.
            // When _next returns (whether detection blocked or allowed the request),
            // the broadcast middleware ALWAYS runs and records the result.
            // This solves the "blocked requests invisible in dashboard" problem.
            app.UseMiddleware<DetectionBroadcastMiddleware>();
        }

        // Detection middleware
        app.UseBotDetection();

        // URL-rewrite (signal-injection) middleware. No-op unless
        // BotDetection:UrlRewrite:Enabled is true. Sits right after detection so
        // every downstream handler — dashboard, MVC, YARP — sees the rewritten
        // query string. See UrlRewriteSignalsMiddleware for the security model.
        app.UseBotDetectionUrlRewrite();

        // X-Bot-Detection-* edge headers on the proxied request. Required for any
        // downstream dashboard host (or YARP upstream that wants the verdict) to
        // resolve identity via StyloBotForwardedHeadersHydratorMiddleware. No-op
        // when BotDetection:ForwardedHeaders:EmitOnForwardedRequest = false.
        // Mirrors the wiring in Stylobot.Gateway / Console hosts so the all-in-one
        // (Stylobot.All) and any host using UseStyloBot() never miss this step.
        app.UseStyloBotForwardedHeaders();

        if (options?.Enabled == true)
        {
            // Admin endpoints (POST /stylobot/admin/{reload,restart}). The middleware
            // short-circuits any non-admin path immediately and 404s admin paths when
            // no token is configured, so it's safe to register unconditionally here.
            // Must run BEFORE StyloBotDashboardMiddleware so the dashboard never sees
            // the admin path.
            app.UseMiddleware<StyloBotAdminMiddleware>();

            // Dashboard UI middleware
            app.UseMiddleware<StyloBotDashboardMiddleware>();

            // SignalR hub for live updates
            // Use IEndpointRouteBuilder directly (not UseEndpoints) to avoid creating
            // a terminal middleware that blocks endpoint routing for later MapGroup/MapGet calls.
            if (app is IEndpointRouteBuilder routeBuilder)
            {
                routeBuilder.MapHub<StyloBotDashboardHub>(options.HubPath)
                    .WithMetadata(new BotDetection.Attributes.BotPolicyAttribute("default") { BlockThreshold = 0.95 });

                // Mount Identity API endpoints at /_stylobot/auth/* when auth is enabled.
                // StyloBotDashboardMiddleware bypasses these paths to let endpoint routing handle them.
                if (options.RequireAuthentication)
                    routeBuilder.MapGroup(options.BasePath.TrimEnd('/') + "/auth")
                        .MapIdentityApi<StyloBotUser>();
            }
        }

        return app;
    }

    /// <summary>
    ///     Maps Stylobot Dashboard endpoints (UI and SignalR hub).
    ///     Prefer <see cref="UseStyloBot"/> which handles middleware ordering automatically.
    ///     Use this only if you need to register detection and dashboard middleware separately.
    /// </summary>
    public static IApplicationBuilder UseStyloBotDashboard(this IApplicationBuilder app)
    {
        var options = app.ApplicationServices.GetRequiredService<StyloBotDashboardOptions>();

        if (!options.Enabled) return app;

        app.UseMiddleware<DetectionBroadcastMiddleware>();
        app.UseMiddleware<StyloBotDashboardMiddleware>();

        if (app is IEndpointRouteBuilder routeBuilder2)
        {
            routeBuilder2.MapHub<StyloBotDashboardHub>(options.HubPath)
                .WithMetadata(new BotDetection.Attributes.BotPolicyAttribute("default") { BlockThreshold = 0.95 });
        }

        return app;
    }

    /// <summary>
    ///     Quick setup: Adds services and middleware with authorization filter.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="authFilter">Authorization filter (return true to allow, false to deny)</param>
    /// <param name="configure">Additional configuration options</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddStyloBotDashboard(
        this IServiceCollection services,
        Func<HttpContext, Task<bool>> authFilter,
        Action<StyloBotDashboardOptions>? configure = null)
    {
        return services.AddStyloBotDashboard(options =>
        {
            options.AuthorizationFilter = authFilter;
            configure?.Invoke(options);
        });
    }

    // ==========================================
    // Widget embedding tier (no full dashboard)
    // ==========================================

    /// <summary>
    ///     Adds the minimal services needed to embed StyloBot widgets in your own pages
    ///     using tag helpers like <c>&lt;sb-visitor-list /&gt;</c>, <c>&lt;sb-summary-stats /&gt;</c>, etc.
    ///     <para>
    ///     Does NOT register the full dashboard route, the help system, or background hosted services
    ///     (no <see cref="DashboardSummaryBroadcaster"/>, no warmup service).
    ///     Use <see cref="AddStyloBotDashboard(IServiceCollection, Action{StyloBotDashboardOptions}?)"/>
    ///     if you want the full <c>/_stylobot</c> dashboard.
    ///     </para>
    ///     <para>
    ///     Pair with <c>app.UseMiddleware&lt;SbWidgetBatchMiddleware&gt;()</c> to enable the
    ///     <c>GET {basePath}/partials/update?widgets=w1,w2</c> HTMX batch-update endpoint
    ///     that powers live widget refreshes.
    ///     </para>
    /// </summary>
    /// <example>
    ///     <code>
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddStyloBotWidgets(new StyloBotDashboardOptions { BasePath = "/_sb" });
    ///     // ...
    ///     app.UseBotDetection();
    ///     app.UseMiddleware&lt;SbWidgetBatchMiddleware&gt;();
    ///     </code>
    /// </example>
    public static IServiceCollection AddStyloBotWidgets(
        this IServiceCollection services,
        StyloBotDashboardOptions? dashboardOptions = null)
    {
        // Tag helpers, DetectionDataExtractor
        services.AddStyloBotUI();

        services.AddMemoryCache();
        services.AddSignalR();

        // Ensure MVC/Razor services are available for view rendering (idempotent)
        services.AddControllersWithViews();

        if (dashboardOptions != null)
            services.AddSingleton(dashboardOptions);
        else
            services.TryAddSingleton(new StyloBotDashboardOptions());

        // Razor view renderer used by SbWidgetBatchMiddleware
        services.TryAddSingleton<RazorViewRenderer>();

        // Liquid template renderer for Node SDK widget rendering
        services.TryAddSingleton<LiquidWidgetRenderer>();

        // Dashboard event store: SQLite for FOSS
        services.TryAddSingleton<IDashboardEventStore, SqliteDashboardEventStore>();

        // Aggregate cache - populated by beacon, read by widget render helpers
        services.TryAddSingleton<DashboardAggregateCache>();

        // Write-through signature cache
        services.TryAddSingleton<SignatureAggregateCache>();

        // Stateless UA aggregator - used by view components with audience/range params
        services.TryAddSingleton<DashboardUserAgentAggregator>();

        return services;
    }

    /// <summary>
    ///     Adds the <see cref="SbWidgetBatchMiddleware"/> to the pipeline so that
    ///     <c>GET {basePath}/partials/update?widgets=w1,w2</c> HTMX batch-update requests are handled.
    ///     Call after <c>app.UseBotDetection()</c>.
    /// </summary>
    /// <example>
    ///     <code>
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddStyloBotWidgets();
    ///     // ...
    ///     app.UseBotDetection();
    ///     app.UseStyloBotWidgets();
    ///     </code>
    /// </example>
    public static IApplicationBuilder UseStyloBotWidgets(this IApplicationBuilder app)
    {
        app.UseMiddleware<SbWidgetBatchMiddleware>();
        return app;
    }

    // ==========================================
    // Lightweight persistence (for gateways/proxies)
    // ==========================================

    /// <summary>
    ///     Adds detection persistence services WITHOUT the full dashboard UI.
    ///     Use this in gateways/proxies that run detection and should save results
    ///     to the shared database, but don't serve the dashboard page.
    ///     <para>
    ///     Registers: event store, SignalR hub, broadcast middleware, visitor cache.
    ///     Does NOT register: dashboard UI, simulator, ViewComponent data extraction.
    ///     </para>
    /// </summary>
    /// <example>
    ///     Gateway setup:
    ///     <code>
    ///     builder.Services.AddBotDetection();
    ///     builder.Services.AddBotDetectionPersistence();
    ///     // ...
    ///     app.UseBotDetection();
    ///     app.UseBotDetectionPersistence(); // saves detections to shared DB
    ///     </code>
    /// </example>
    public static IServiceCollection AddBotDetectionPersistence(this IServiceCollection services)
    {
        // Shared options (Enabled=true but no UI path needed)
        services.TryAddSingleton(new StyloBotDashboardOptions { Enabled = true });

        // SignalR for broadcasting to connected dashboard clients
        services.AddSignalR();

        // Event store: SQLite by default (persists across restarts).
        // PostgreSQL package overrides via RemoveAll + AddSingleton when configured.
        services.TryAddSingleton<IDashboardEventStore, SqliteDashboardEventStore>();

        // Operator-supplied signature labels for detector weighting / ground truth
        // (in-memory by default; production wires a SQLite or PostgreSQL implementation).
        services.TryAddSingleton<ISignatureLabelStore, SqliteSignatureLabelStore>();

        // Visitor "flag as wrong" feedback store. Owned by the enforcement component
        // (this host runs detection); the upstream site POSTs to /api/v1/feedback
        // rather than writing it directly. PostgreSQL pack overrides via RemoveAll.
        services.TryAddSingleton<IDetectionFeedbackStore, SqliteDetectionFeedbackStore>();

        // Aggregate cache - populated by beacon, read by API endpoints
        services.TryAddSingleton<DashboardAggregateCache>();

        // Write-through signature cache - single source of truth for top bots
        services.TryAddSingleton<SignatureAggregateCache>();

        // Stateless UA aggregator - used by broadcaster beacon + view components with params
        services.TryAddSingleton<DashboardUserAgentAggregator>();

        // Warm the SignatureAggregateCache from DB on startup so Top Bots / Live Activity /
        // visitor list aren't empty after restart. Cache is in-memory read-through;
        // persistence is the source of truth in IDashboardEventStore.
        //
        // Gateway-only per project_gateway_data_locality. On remote-mode hosts
        // (marketing site, stylobot-ui) the warmup would issue DB queries against
        // a store that doesn't belong to this process AND freeze the cache at a
        // stale startup snapshot — DetectionBroadcastMiddleware runs only on the
        // gateway, so nothing refreshes the cache afterwards. That produces the
        // classic "home YOU card shows 100% / signature detail shows 7% for the
        // same sig" divergence: home reads cache (stale), detail reads event
        // store (current). Skip the warmup when AddStyloBotDashboardRemote has
        // already registered DashboardSourceOptions; the cache stays registered
        // (satisfies DI for middleware / view-components) but stays empty, so
        // all read paths cleanly fall through to IDashboardEventStore — which
        // on remote-mode hosts is RemoteDashboardEventStore (REST → gateway).
        // One source of truth.
        if (!services.Any(s => s.ServiceType == typeof(Mostlylucid.BotDetection.UI.Adapters.Remote.DashboardSourceOptions)))
        {
            services.AddHostedService<SignatureAggregateCacheWarmupService>();
        }

        // LLM result callback (needed if LLM classification is enabled)
        services.TryAddSingleton<ILlmResultCallback, LlmResultSignalRCallback>();

        // Cluster description callback for live cluster updates
        services.TryAddSingleton<IClusterDescriptionCallback, ClusterDescriptionSignalRCallback>();

        return services;
    }

    /// <summary>
    ///     Enables the built-in SMTP email sender for dashboard auth flows (confirmation,
    ///     password reset, 2FA codes). Reads configuration from <c>StyloBot:Smtp</c> in
    ///     appsettings.json for the legacy <c>FromName</c> label and from <c>Notify:Email</c>
    ///     for the underlying SMTP transport bound by <c>Mostlylucid.Notify</c>.
    ///     Call this after <see cref="AddStyloBot"/> or
    ///     <see cref="AddStyloBotDashboard(IServiceCollection, Action{StyloBotDashboardOptions}?)"/>.
    ///     <para>
    ///     Without this, StyloBot logs tokens to the console (dev no-op sender).
    ///     Commercial deployments can skip this and register a custom
    ///     <c>IEmailSender&lt;StyloBotUser&gt;</c> directly.
    ///     </para>
    ///     <para>
    ///     CONSUMER MUST CALL after <c>app.Build()</c> so the Notify template registry
    ///     materialises (typed singletons are touched once, registry self-populates):
    ///     <code>
    ///     app.Services.ActivateNotifyTemplates();
    ///     </code>
    ///     This library is a Razor class library, so it cannot reach <c>IHost</c> itself.
    ///     </para>
    /// </summary>
    /// <example>
    ///     <code>
    ///     // appsettings.json:
    ///     // "StyloBot": { "Smtp": { "FromName": "Stylobot Dashboard" } }
    ///     // "Notify": { "Email": { "From": "noreply@example.com",
    ///     //   "Smtp": { "Host": "smtp.example.com", "Port": 587,
    ///     //     "User": "user", "Password": "pass", "UseTls": true } } }
    ///     builder.Services.AddStyloBot(d => d.RequireAuthentication = true);
    ///     builder.Services.AddStyloBotSmtp(builder.Configuration);
    ///     // ...
    ///     var app = builder.Build();
    ///     app.Services.ActivateNotifyTemplates();
    ///     app.Services.StartNotifyDrain(app.Lifetime.ApplicationStopping);
    ///     </code>
    /// </example>
    public static IServiceCollection AddStyloBotSmtp(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddOptions<StyloBotSmtpOptions>()
            .BindConfiguration(StyloBotSmtpOptions.Section);

        // Wire the Notify pipeline that StyloBotSmtpEmailSender shims onto. Each typed
        // Identity callback maps to one of the three M0 templates. EmailSender direct-sends
        // via MailKit (synchronous, no outbox/drain in v0.1) -- so no outbox wiring and no
        // StartDrainOnCoordinator. AddNotifyOutbox* + StartDrainOnCoordinator were dead in
        // v0.1 (EmailSender doesn't enqueue) AND the drain starter requires an
        // IEphemeralCoordinator that hosts don't always pre-register, crashing startup.
        // Outbox-backed retry returns in Notify 0.1.2+ once the library bootstraps its own
        // coordinator.
        services.AddNotify(configuration)
            .AddNotifyEmail()
            .AddEmailTemplate<Notifications.RegistrationVerifyModel, Notifications.RegistrationVerifyEmail>("registration.verify")
            .AddEmailTemplate<Notifications.PasswordResetModel, Notifications.PasswordResetEmail>("auth.password.reset")
            .AddEmailTemplate<Notifications.MfaCodeModel, Notifications.MfaCodeEmail>("auth.mfa.code");

        // Remove dev no-op and register SMTP sender (now a Notify shim). The class is
        // [Obsolete] for direct consumption but DI registration of an obsolete type is
        // intentional here -- the legacy IEmailSender<StyloBotUser> contract still drives
        // ASP.NET Identity callbacks.
#pragma warning disable CS0618
        services.RemoveAll<IEmailSender<StyloBotUser>>();
        services.AddTransient<IEmailSender<StyloBotUser>, StyloBotSmtpEmailSender>();
#pragma warning restore CS0618

        return services;
    }

    /// <summary>
    ///     Adds the detection broadcast middleware that persists detection results
    ///     to the event store and broadcasts via SignalR.
    ///     Use after <see cref="Mostlylucid.BotDetection.Middleware.BotDetectionMiddlewareExtensions.UseBotDetection"/>.
    ///     <para>
    ///     This is the lightweight counterpart to <see cref="UseStyloBotDashboard"/> -
    ///     it saves detection data but doesn't serve the dashboard UI.
    ///     </para>
    /// </summary>
    public static IApplicationBuilder UseBotDetectionPersistence(this IApplicationBuilder app)
    {
        // Broadcast middleware: persists detections to event store + broadcasts to SignalR
        app.UseMiddleware<DetectionBroadcastMiddleware>();

        // Map SignalR hub so dashboard clients (on other hosts) can connect
        var options = app.ApplicationServices.GetService<StyloBotDashboardOptions>();
        var hubPath = options?.HubPath ?? "/stylobot/hub";
        if (app is IEndpointRouteBuilder erb)
            erb.MapHub<StyloBotDashboardHub>(hubPath);

        return app;
    }

    /// <summary>
    ///     Replace the default <see cref="Mostlylucid.BotDetection.Services.NullEndpointPerfBaseline"/>
    ///     with the <see cref="Mostlylucid.BotDetection.UI.Services.DashboardEventStoreBackedEndpointPerfBaseline"/>
    ///     that reads per-(method, normalized-template) p95 from
    ///     <see cref="Mostlylucid.BotDetection.UI.Services.IDashboardEventStore"/>. Call this from the
    ///     gateway / dashboard-host bootstrap AFTER
    ///     <c>AddBotDetectionDashboard()</c> so the store is registered first.
    /// </summary>
    public static IServiceCollection AddDashboardEndpointPerfBaseline(this IServiceCollection services)
    {
        Microsoft.Extensions.DependencyInjection.Extensions.ServiceCollectionDescriptorExtensions
            .RemoveAll<Mostlylucid.BotDetection.Services.IEndpointPerfBaseline>(services);
        services.AddSingleton<Mostlylucid.BotDetection.Services.IEndpointPerfBaseline,
            Mostlylucid.BotDetection.UI.Services.DashboardEventStoreBackedEndpointPerfBaseline>();
        return services;
    }
}