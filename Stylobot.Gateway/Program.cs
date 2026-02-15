using System.Net;
using Microsoft.AspNetCore.HttpOverrides;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.PostgreSQL.Extensions;
using Mostlylucid.GeoDetection.Contributor.Extensions;
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
    Log.Information("Starting Stylobot.Gateway v0.1");

    var builder = WebApplication.CreateBuilder(args);

    // Configure Kestrel shutdown timeout so keep-alive connections drain before SIGKILL
    builder.WebHost.ConfigureKestrel(options =>
    {
        options.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(5);
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
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
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

    // Add gateway configuration
    builder.Services.AddGatewayConfiguration(builder.Configuration);

    // Add database if configured
    builder.Services.AddGatewayDatabase(builder.Configuration);

    // Add Bot Detection - the core feature of this gateway!
    // Uses appsettings.json "BotDetection" section automatically
    builder.Services.AddBotDetection();

    // Add geo detection for country code enrichment on all requests
    builder.Services.AddGeoDetectionContributor(options =>
    {
        options.EnableBotVerification = true;
        options.EnableInconsistencyDetection = true;
        options.FlagHostingIps = true;
        options.FlagVpnIps = false;
    });

    // Add detection persistence: saves detections to shared DB + broadcasts via SignalR.
    // This is the lightweight path (no dashboard UI served from the gateway).
    // The website handles dashboard rendering; the gateway just persists and broadcasts.
    builder.Services.AddBotDetectionPersistence();

    // Add PostgreSQL persistence if connection string is configured
    var pgConnectionString = builder.Configuration["StyloBotDashboard:PostgreSQL:ConnectionString"]
                             ?? Environment.GetEnvironmentVariable("STYLOBOT_PG_CONNECTION");
    if (!string.IsNullOrEmpty(pgConnectionString))
    {
        builder.Services.AddStyloBotPostgreSQL(pgConnectionString, options =>
        {
            options.EnableTimescaleDB = builder.Configuration.GetValue("StyloBotDashboard:PostgreSQL:EnableTimescaleDB", true);
            options.AutoInitializeSchema = builder.Configuration.GetValue("StyloBotDashboard:PostgreSQL:AutoInitializeSchema", true);
            options.RetentionDays = builder.Configuration.GetValue("StyloBotDashboard:PostgreSQL:RetentionDays", 90);
            var compressionDays = builder.Configuration.GetValue("StyloBotDashboard:PostgreSQL:CompressionAfterDays", 7);
            options.CompressionAfter = TimeSpan.FromDays(compressionDays);
        });
        Log.Information("Gateway persistence: PostgreSQL enabled (TimescaleDB={Timescale})",
            builder.Configuration.GetValue("StyloBotDashboard:PostgreSQL:EnableTimescaleDB", true));
    }
    else
    {
        Log.Information("Gateway persistence: in-memory (no PostgreSQL connection string configured)");
    }

    // Configure demo mode if enabled
    ConfigureDemoMode(builder.Configuration, builder.Services);

    // Add YARP reverse proxy
    builder.Services.AddYarpServices(builder.Configuration);

    // Add metrics and health
    builder.Services.AddGatewayServices();

    // Add health checks
    builder.Services.AddGatewayHealthChecks(builder.Configuration);

    var app = builder.Build();

    // Apply database migrations if enabled
    await app.ApplyMigrationsAsync();

    // Forward headers FIRST so bot detection sees real client IPs, not Docker bridge IPs
    app.UseForwardedHeaders();

    // Routing must be enabled for Bot Detection middleware to resolve endpoints
    app.UseRouting();

    // Admin secret middleware (if configured)
    app.UseAdminSecretMiddleware();

    // Bot Detection middleware - runs on every request
    app.UseBotDetection();

    // Persist detections to shared DB + broadcast via SignalR
    // Downstream dashboard clients (on the website) can connect to this hub
    app.UseBotDetectionPersistence();

    // Admin API endpoints
    app.MapAdminEndpoints();

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
