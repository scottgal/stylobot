using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.OpenApi;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Auth;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.Auth;
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

        // Register the named HTTP client used for API calls.
        services.AddHttpClient("stylobot");

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
        services.AddSingleton<IOptions<StyloBotDashboardOptions>>(Options.Create(options));

        // Register lightweight UI services (tag helpers, view components)
        services.AddStyloBotUI();

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
        services.TryAddSingleton<Mostlylucid.BotDetection.Policies.Signals.ISignalCatalog>(_ =>
        {
            var asm = typeof(Mostlylucid.BotDetection.Models.SignalKeys).Assembly;
#pragma warning disable IL2026 // SignalCatalog.LoadAsync reflects const fields; overlays are pre-registered for AOT.
            return Mostlylucid.BotDetection.Policies.Signals.SignalCatalog
                .LoadAsync(asm).GetAwaiter().GetResult();
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
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyStackPresenter>();
        // C6 expression-editor presenter. Pure read surface; never mutates a
        // rule. The actual write goes through the commercial mutation API
        // (/api/v1/policies, C3); this presenter just shapes the existing
        // rule (or empty defaults) into the edit-row view model.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.PolicyEditPresenter>();

        // Pack Metrics B1 -- /dashboard/insights page composer. Pure read; goes
        // through IMeterStream only (no DB). Stateless, peer-singleton with the
        // other dashboard presenters above. The IMeterStream binding itself is
        // owned by PrometheusPack (AddPrometheusPack registers Local or Remote).
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Services.InsightsPageBuilder>();

        // B6 -- Policy Stack live-update beacon. SignalR by default; commercial
        // packs replace this with a Redis-fanned implementation so an edit on
        // one node reaches every other node's connected browsers. The hosted
        // service bridges the rule store's Changed event into the broadcaster
        // and stays running for the host lifetime.
        services.TryAddSingleton<Mostlylucid.BotDetection.UI.Policies.IPolicyChangeBroadcaster,
            Mostlylucid.BotDetection.UI.Policies.SignalRPolicyChangeBroadcaster>();
        services.AddHostedService<Mostlylucid.BotDetection.UI.Policies.PolicyChangeNotificationHostedService>();

        // Dashboard tooltip registry — loads Definitions/Tooltips/*.yaml at
        // startup. Cheap to register unconditionally; the helper short-circuits
        // when StyloBotDashboardOptions.EnableTooltips is false so the resolved
        // registry never gets queried on FOSS hosts that haven't opted in.
        services.AddSingleton<DashboardTooltipRegistry>();

        // Static detection-side data the dashboard renders need. Registered as
        // TryAddSingleton so that hosts which also call AddBotDetection get
        // those richer registrations instead. Pure dashboard-viewer hosts
        // (header-driven, no detection pipeline, no DB) get just these stubs:
        //
        //   - IdentityVectorLayout: the slot map (static, embedded resources).
        //   - IdentityVectorEncoder: stateless wrapper around the layout.
        //   - IdentityArchetypeRegistry: archetype dictionary loaded from
        //     YAML embedded in Mostlylucid.BotDetection.
        //   - DomainEntitlementValidator: license-domain warn-only host check,
        //     not part of detection -- registered idempotently here so the
        //     UseDomainEntitlement middleware works in viewer hosts too.
        //
        // None of these touch a database or run the detection pipeline.
        services.TryAddSingleton(sp => IdentityVectorLayout.DefaultV1());
        services.TryAddSingleton<IdentityVectorEncoder>();
        services.TryAddSingleton<IdentityArchetypeRegistry>();
        services.AddDomainEntitlement();

        // Dashboard event store: SQLite for FOSS (persists across restarts).
        // Commercial PostgreSQL package overrides via TryAddSingleton.
        services.TryAddSingleton<IDashboardEventStore, SqliteDashboardEventStore>();

        // Operator-supplied signature labels (for detector weighting / ground truth).
        // In-memory by default; production hosts can register SQLite / PostgreSQL impls.
        services.TryAddSingleton<ISignatureLabelStore, SqliteSignatureLabelStore>();

        // Aggregate cache - populated by beacon, read by API endpoints
        services.AddSingleton<DashboardAggregateCache>();

        // Write-through signature cache - single source of truth for top bots
        services.AddSingleton<SignatureAggregateCache>();

        // Stateless UA aggregator - used by broadcaster beacon + view components with params
        services.TryAddSingleton<DashboardUserAgentAggregator>();

        // Background beacon - computes all dashboard aggregates periodically
        services.AddHostedService<DashboardSummaryBroadcaster>();

        // Server-side visitor cache for HTMX rendering
        services.AddSingleton<VisitorListCache>();

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

        // Warm visitor cache from DB on startup so the visitors tab isn't empty after restart.
        services.AddHostedService<VisitorCacheWarmupService>();
        // Same fail-soft warm for the SignatureAggregateCache that feeds Top Bots / Live
        // Activity / live-visitors widgets. Without this, every restart wiped the cache
        // and "Top Bots" stayed empty until fresh traffic arrived (cache is in-memory
        // read-through; persistence is the source of truth in IDashboardEventStore).
        services.AddHostedService<SignatureAggregateCacheWarmupService>();

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
        services.AddHttpClient("stylobot-openapi");
        services.TryAddSingleton<IOpenApiDocumentLoader, OpenApiDocumentLoader>();
        // Bind from the StyloBotDashboardOptions.OpenApi instance the user already configured.
        services.AddSingleton<IOptions<OpenApiSeedOptions>>(sp =>
            Options.Create(sp.GetRequiredService<StyloBotDashboardOptions>().OpenApi));
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
                services.AddHostedService<MeterListenerService>(sp =>
                    new MeterListenerService(
                        sp.GetServices<IMonitoringPack>(),
                        sp.GetRequiredService<IMetricSnapshotStore>(),
                        sp.GetRequiredService<ILogger<MeterListenerService>>(),
                        sp.GetRequiredService<IPackRuntimeController>()));
            }
            else if (options.MonitoringPack.Mode == MonitoringMode.GatewayServer)
            {
                services.AddSingleton<IMonitoringPack>(
                    new AspNetMonitoringPack(options.MonitoringPack.IncludeAspNetHostMeters));
                services.AddSingleton<GatewayMeterAccumulator>(sp =>
                    new GatewayMeterAccumulator(
                        sp.GetServices<IMonitoringPack>(),
                        sp.GetRequiredService<ILogger<GatewayMeterAccumulator>>()));
                services.AddHostedService(sp => sp.GetRequiredService<GatewayMeterAccumulator>());
            }
            else if (options.MonitoringPack.Mode == MonitoringMode.RemoteClient
                     && options.MonitoringPack.GatewayMetricsUrl != null)
            {
                services.AddHttpClient("sb-metrics");
                services.AddHostedService<RemoteMetricCollector>(sp =>
                    new RemoteMetricCollector(
                        sp.GetRequiredService<IHttpClientFactory>(),
                        options.MonitoringPack.GatewayMetricsUrl,
                        options.MonitoringPack.RemotePollInterval,
                        sp.GetRequiredService<IMetricSnapshotStore>(),
                        sp.GetRequiredService<ILogger<RemoteMetricCollector>>()));
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
            services.TryAddTransient<IEmailSender<StyloBotUser>, StyloBotDevEmailSender>();
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

        // Server-side visitor cache used by the visitor-list widget
        services.TryAddSingleton<VisitorListCache>();

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

        // Aggregate cache - populated by beacon, read by API endpoints
        services.TryAddSingleton<DashboardAggregateCache>();

        // Write-through signature cache - single source of truth for top bots
        services.TryAddSingleton<SignatureAggregateCache>();

        // Stateless UA aggregator - used by broadcaster beacon + view components with params
        services.TryAddSingleton<DashboardUserAgentAggregator>();

        // Server-side visitor cache (needed by broadcast middleware)
        services.TryAddSingleton<VisitorListCache>();

        // Warm visitor cache from DB on startup so the visitors tab isn't empty after restart.
        services.AddHostedService<VisitorCacheWarmupService>();
        // Same fail-soft warm for the SignatureAggregateCache that feeds Top Bots / Live
        // Activity / live-visitors widgets. Without this, every restart wiped the cache
        // and "Top Bots" stayed empty until fresh traffic arrived (cache is in-memory
        // read-through; persistence is the source of truth in IDashboardEventStore).
        services.AddHostedService<SignatureAggregateCacheWarmupService>();

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
}