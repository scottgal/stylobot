using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Mostlylucid.BotDetection.EndpointPolicies;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Honeypot;
using Mostlylucid.BotDetection.Licensing;
using Mostlylucid.BotDetection.Llm.LlamaSharp.Extensions;
using Mostlylucid.BotDetection.Llm.Ollama.Extensions;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Telemetry;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.StyloExtract.Extensions;
// PostgreSQL dashboard persistence is in the commercial repo (stylobot-commercial)
using Mostlylucid.GeoDetection.Extensions;
using StyloExtract.AspNetCore;
using Mostlylucid.GeoDetection.Models;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Microsoft.Extensions.Options;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Data;
using Stylobot.Gateway.Endpoints;
using Stylobot.Gateway.Middleware;
using Stylobot.Gateway.Services;
using Serilog;

// Configure Serilog early for startup logging
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateBootstrapLogger();

try
{
    var earlyConfig = new ConfigurationBuilder()
        .AddJsonFile(Path.Combine(GatewayPaths.Config, "appsettings.json"), optional: true)
        .AddEnvironmentVariables()
        .Build();
    var earlyTlsForBanner = Stylobot.Gateway.Configuration.ServiceCollectionExtensions.ReadTlsOptionsFromEnv();
    StartupBanner.Print(earlyConfig, earlyTlsForBanner);
    Log.Information("Starting Stylobot.Gateway");

    var builder = WebApplication.CreateBuilder(args);

    // Configure Kestrel: accept H1 and H2C (cleartext HTTP/2).
    // H2C is required when cloudflared has http2Origin: true - cloudflared speaks H2C to the origin.
    // Without this, cloudflared falls back to H1 and loses multiplexing benefits.
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
        options.ConfigureEndpointDefaults(listenOptions =>
        {
            listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
        });
    });
    builder.Host.ConfigureHostOptions(options =>
    {
        options.ShutdownTimeout = TimeSpan.FromSeconds(30);
    });

    // Configure Serilog from configuration
    builder.Host.UseSerilog((context, services, configuration) =>
    {
        configuration
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .Enrich.FromLogContext()
            .WriteTo.Console(
                outputTemplate: "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}");

        // Write to file if logs directory is writable
        var logsPath = GatewayPaths.Logs;
        if (Directory.Exists(logsPath))
        {
            configuration.WriteTo.File(
                Path.Combine(logsPath, "gateway-.log"),
                rollingInterval: RollingInterval.Day,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}");
        }
    });

    // Forward headers from reverse proxy (Caddy) so bot detection sees the real client IP.
    // Without this, the gateway sees Caddy's Docker bridge IP (172.x.x.x) instead.
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        var trustAllProxies = builder.Environment.IsDevelopment() ||
                              builder.Configuration.GetValue("Network:TrustAllForwardedProxies", false) ||
                              bool.TryParse(Environment.GetEnvironmentVariable("TRUST_ALL_FORWARDED_PROXIES"), out var trustAll) &&
                              trustAll;

        if (trustAllProxies)
        {
            if (!builder.Environment.IsDevelopment())
                Log.Warning("TrustAllForwardedProxies is enabled outside Development. " +
                            "This allows IP spoofing via X-Forwarded-For. " +
                            "Configure Network:KnownNetworks/KnownProxies for production.");
            // Trusting "all" proxies in ASP.NET requires explicit any-network
            // entries -- the default behaviour when KnownProxies / KnownIPNetworks
            // are both empty is to IGNORE X-Forwarded-For entirely, so the
            // pipeline sees the docker bridge IP for every external request.
            // That misclassifies tunnel-exit traffic as a single internal client,
            // collapses every visitor onto the same fingerprint, and trips the
            // generic-Tool throttle on the home dashboard. Add the IPv4 and IPv6
            // "any" networks so every upstream proxy is trusted; uncap
            // ForwardLimit so a request behind multiple hops (e.g. browser ->
            // Cloudflare -> cloudflared -> reverse-proxy -> gateway) still
            // walks the chain to the first untrusted client.
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.Any, 0));
            options.KnownIPNetworks.Add(new System.Net.IPNetwork(IPAddress.IPv6Any, 0));
            options.ForwardLimit = null;
            return;
        }

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var knownNetworkList = builder.Configuration["Network:KnownNetworks"] ??
                               Environment.GetEnvironmentVariable("KNOWN_NETWORKS") ??
                               string.Empty;
        foreach (var network in knownNetworkList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = network.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 2 &&
                IPAddress.TryParse(parts[0], out var prefix) &&
                int.TryParse(parts[1], out var prefixLength))
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
        }

        var knownProxyList = builder.Configuration["Network:KnownProxies"] ??
                             Environment.GetEnvironmentVariable("KNOWN_PROXIES") ??
                             string.Empty;
        foreach (var proxy in knownProxyList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (IPAddress.TryParse(proxy, out var ip))
                options.KnownProxies.Add(ip);
    });

    // Add gateway configuration (binds env vars including TLS options)
    builder.Services.AddGatewayConfiguration(builder.Configuration);

    var earlyTls = Stylobot.Gateway.Configuration.ServiceCollectionExtensions.ReadTlsOptionsFromEnv();
    builder.Services.AddGatewayTls(earlyTls);
    if (earlyTls.Enabled)
    {
        // ACME cert store must exist before LettuceEncrypt starts; create it here rather than inside DI registration.
        if (earlyTls.IsAcme) Directory.CreateDirectory(earlyTls.AcmeCertStorePath);
        Log.Information("TLS mode: {Mode}, port {Port}",
            earlyTls.IsAcme ? $"ACME ({earlyTls.Domain})" : $"cert-from-file ({earlyTls.CertPath})",
            earlyTls.Port);
    }

    // Add database if configured
    builder.Services.AddGatewayDatabase(builder.Configuration);

    // Add Bot Detection - the core feature of this gateway!
    // Uses appsettings.json "BotDetection" section automatically
    builder.Services.AddBotDetection();

    // StyloExtract action policies: registers extract-markdown / extract-headers /
    // extract-sidecar / extract-passthrough into the IActionPolicyRegistry. The
    // BotDetection middleware (UseBotDetection below) dispatches them by name from
    // BotDetection:DetectionPolicies:Rules so AI scrapers visiting /docs paths can
    // be served clean Markdown instead of HTML. Body interception happens here at
    // the gateway because YARP terminates the upstream response; the website is a
    // pure dashboard viewer with no detection middleware in its pipeline.
    // AddStyloExtract registers the extractor + SQLite template store; the pack's
    // four IActionPolicy entries are wired by AddStyloExtractActionPolicies.
    builder.Services.AddStyloExtract();
    builder.Services.AddStyloExtractActionPolicies();

    // LLM provider for background classification, bot naming, and score-change
    // narratives. When BotDetection:AiDetection:Provider=ollama (or the env var
    // BOTDETECTION__AIDETECTION__PROVIDER=ollama), register the HTTP Ollama
    // client pointing at the configured endpoint. Otherwise fall back to the
    // in-process LlamaSharp CPU provider. Both bind their own config sections
    // (BotDetection:AiDetection:Ollama / :LlamaSharp), so swapping is a single
    // env var on the staging / prod compose files.
    var llmProvider = (builder.Configuration["BotDetection:AiDetection:Provider"] ?? "llamasharp")
        .Trim().ToLowerInvariant();
    if (llmProvider == "ollama")
    {
        builder.Services.AddStylobotOllama();
    }
    else
    {
        builder.Services.AddStylobotLlamaSharp();
    }

    // Add OpenTelemetry instrumentation for bot detection signals
    builder.Services.AddBotDetectionTelemetry();

    // Wire up OTel SDK - Prometheus exporter for /metrics scraping
    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(BotDetectionMetrics.MeterName)
            .AddMeter(BotDetectionSignalMeter.MeterName)
            .AddPrometheusExporter())
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(BotDetectionTelemetry.ActivitySourceName));

    // Add geo detection services (IP lookup via ip-api.com fallback)
    builder.Services.AddGeoRouting(
        configureRouting: options =>
        {
            options.Enabled = true;
            options.AddCountryHeader = true;
            options.StoreInContext = true;
        },
        configureProvider: options =>
        {
            options.Provider = GeoProvider.IpApi;
            options.FallbackToSimple = true;
        });

    // NOTE: GeoDetectionContributor removed from detection pipeline to eliminate ~50ms
    // external API call latency. Country code is still available from GeoRouting middleware
    // context (HttpContext.Items["GeoLocation"]) - the broadcast middleware reads it as fallback.

    // Add detection persistence: saves detections to shared DB + broadcasts via SignalR.
    // This is the lightweight path (no dashboard UI served from the gateway).
    // The website handles dashboard rendering; the gateway just persists and broadcasts.
    builder.Services.AddBotDetectionPersistence();

    // PostgreSQL persistence is a commercial feature (stylobot-commercial repo)
    Log.Information("Gateway persistence: SQLite (FOSS default)");

    // Configure demo mode if enabled
    ConfigureDemoMode(builder.Configuration, builder.Services);

    // Add YARP reverse proxy
    builder.Services.AddYarpServices(builder.Configuration);

    // Add metrics and health
    builder.Services.AddGatewayServices();

    // Add profile mode services (channel, calibration store, background worker)
    builder.Services.AddProfileMode(builder.Configuration);

    // Configure profile mode policy override if enabled
    ConfigureProfileMode(builder.Configuration, builder.Services);

    // Add health checks
    builder.Services.AddGatewayHealthChecks(builder.Configuration);

    var app = builder.Build();

    // Apply database migrations if enabled
    await app.ApplyMigrationsAsync();

    // Initialize profile calibration store (no-op when profile mode disabled)
    await app.InitializeProfileStoreAsync();

    // Forward headers FIRST so bot detection sees real client IPs, not Docker bridge IPs
    app.UseForwardedHeaders();

    // Anti-spoofing: drop X-Bot-Detection-* verdict headers a visitor attached
    // (this gateway computes its own and re-emits them via UseStyloBotForwardedHeaders),
    // and — when ForwardedHeaders:StripInboundClientSignalHeaders is enabled —
    // client-signal headers (X-JA3-*, X-Client-TLS-*, …) that only a trusted
    // upstream proxy may inject. Must run before anything reads request headers.
    app.UseStyloBotInboundClientHeaderStrip();

    // TLS metadata (protocol + cipher suite) → HttpContext.Items for JA3/JA4 fingerprinting.
    // Only active when the gateway terminates TLS itself (cert-from-file or ACME modes).
    if (earlyTls.Enabled)
        app.UseTlsMetadataCapture();

    // WebSockets must be enabled before routing so YARP can proxy SignalR WebSocket connections
    app.UseWebSockets();

    // Routing must be enabled for Bot Detection middleware to resolve endpoints
    app.UseRouting();

    // Admin secret middleware (if configured)
    app.UseAdminSecretMiddleware();

    // Profile capture middleware: records request snapshots for background calibration analysis.
    // Runs after admin secret middleware so admin requests are also captured for baseline stats.
    // Runs before geo routing and bot detection so it sees every inbound request.
    app.UseMiddleware<Stylobot.Gateway.Middleware.ProfileCaptureMiddleware>();

    // Geo routing - enriches requests with country code from IP (cached per IP)
    // Must run BEFORE bot detection so country data is available for detection + dashboard
    app.UseGeoRouting();

    // License entitlement: warn-never-lock host check against BotDetection:Licensing:Domains.
    // No-op when no domains configured. Stashes mismatch counters for the dashboard's
    // license card; never affects request flow.
    app.UseDomainEntitlement();

    // Bot Detection middleware - runs on every request
    app.UseBotDetection();

    // DetectionPolicyMiddleware: dispatches IActionPolicy entries by name from
    // BotDetection:DetectionPolicies:Rules based on the detection verdict.
    // Required for the extract-markdown / extract-headers / extract-sidecar
    // policies (and the existing block-hard rules) to actually fire — without
    // this hook the rules are evaluated by no one. Must run AFTER UseBotDetection
    // so AggregatedEvidence is on HttpContext, and BEFORE MapReverseProxy so the
    // policy can short-circuit upstream forwarding when a content transform fires.
    app.UseDetectionPolicies();

    // Persist detections to shared DB + broadcast via SignalR
    // Downstream dashboard clients (on the website) can connect to this hub
    app.UseBotDetectionPersistence();

    // Terminate Tier 1 honeypot hits (/.env, /.git/config, /etc/passwd, etc.)
    // with a bare 404 before YARP. Must run AFTER UseBotDetection so the
    // detection event is still written (honeypot panel + threat aggregator
    // see the hit) and BEFORE MapReverseProxy so the upstream origin never
    // answers -- otherwise its own 404/403/200 fingerprint-leaks.
    app.UseHoneypotTermination();

    // Attach X-Bot-Detection-* headers to the proxied request so the downstream
    // dashboard host's StyloBotForwardedHeadersHydratorMiddleware can populate
    // HttpContext.Items[SignalKeys.SignatureMultifactor] etc. without doing its own
    // detection. Required for the website's "You: Bot/Human X%" pill to find the
    // visitor's persisted detection -- otherwise the website regenerates a fresh
    // local signature that doesn't match the gateway's recorded one and the pill
    // sticks on "Detection pending..." forever. Must run AFTER UseBotDetection
    // and BEFORE MapReverseProxy.
    app.UseStyloBotForwardedHeaders();

    // Admin API endpoints
    app.MapAdminEndpoints();

    // Profile mode calibration endpoints (only mapped when profile mode enabled)
    var profileEnabled = app.Services.GetRequiredService<IOptions<ProfileModeOptions>>().Value.Enabled;
    if (profileEnabled)
    {
        var gatewayOpts = app.Services.GetRequiredService<IOptions<GatewayOptions>>().Value;
        var adminPath = gatewayOpts.AdminBasePath;
        app.MapCalibrationEndpoints(adminPath);
    }

    // Prometheus metrics endpoint for scraping
    app.MapPrometheusScrapingEndpoint();

    // YARP reverse proxy
    app.MapReverseProxy();

    // Fallback for no-routes scenario
    app.MapFallback(context =>
    {
        context.Response.ContentType = "application/json";
        context.Response.StatusCode = 503;
        return context.Response.WriteAsJsonAsync(new
        {
            status = "no-routes",
            message = "No YARP routes configured. See /admin/config for details."
        });
    });

    // Register graceful shutdown handlers to drain connections cleanly
    var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
    lifetime.ApplicationStopping.Register(() =>
    {
        Log.Information("Gateway shutting down - draining active connections...");
    });
    lifetime.ApplicationStopped.Register(() =>
    {
        Log.Information("Gateway stopped - all connections drained");
    });

    Log.Information("Gateway started on port {Port}", builder.Configuration.GetValue("GATEWAY_HTTP_PORT", 8080));

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Gateway terminated unexpectedly");
    throw;
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>
/// Configure profile mode: switches all paths to 'profile' policy for fingerprint-only inline
/// detection while background calibration collects threshold data.
/// </summary>
static void ConfigureProfileMode(IConfiguration configuration, IServiceCollection services)
{
    var profileModeEnv = Environment.GetEnvironmentVariable("GATEWAY_PROFILE_MODE");
    var profileModeEnabled = bool.TryParse(profileModeEnv, out var profEnabled) && profEnabled;

    if (!profileModeEnabled)
        profileModeEnabled = configuration.GetValue<bool>("Gateway:ProfileMode:Enabled");

    if (!profileModeEnabled) return;

    var demoModeEnv = Environment.GetEnvironmentVariable("GATEWAY_DEMO_MODE");
    var demoEnabled = (bool.TryParse(demoModeEnv, out var de) && de)
        || configuration.GetValue<bool>("Gateway:DemoMode:Enabled");
    if (demoEnabled)
        Log.Warning("Both GATEWAY_PROFILE_MODE and GATEWAY_DEMO_MODE are set -- profile mode takes precedence");

    Log.Information("Profile mode active -- fingerprint-only inline detection, background calibration enabled");
    services.PostConfigure<BotDetectionOptions>(opts =>
    {
        opts.PathPolicies.Clear();
        opts.PathPolicies["/*"] = "profile";
    });
}

/// <summary>
/// Configure demo mode: switches to 'demo' policy if demo mode is enabled.
/// </summary>
static void ConfigureDemoMode(IConfiguration configuration, IServiceCollection services)
{
    // Check if demo mode is enabled
    var demoModeEnv = Environment.GetEnvironmentVariable("GATEWAY_DEMO_MODE");
    var demoModeEnabled = bool.TryParse(demoModeEnv, out var demoEnabled) && demoEnabled;

    if (!demoModeEnabled)
    {
        demoModeEnabled = configuration.GetValue<bool>("Gateway:DemoMode:Enabled");
    }

    if (!demoModeEnabled)
    {
        return;
    }

    // Override PathPolicies to use 'demo' policy for all paths
    services.PostConfigure<BotDetectionOptions>(opts =>
    {
        // Clear existing path policies and set all paths to 'demo'
        opts.PathPolicies.Clear();
        opts.PathPolicies["/*"] = "demo";

        Log.Information("Demo mode active - using 'demo' policy with ALL detectors enabled");
    });
}