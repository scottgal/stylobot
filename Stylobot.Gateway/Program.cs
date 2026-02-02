using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Middleware;
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

    // Add gateway configuration
    builder.Services.AddGatewayConfiguration(builder.Configuration);

    // Add database if configured
    builder.Services.AddGatewayDatabase(builder.Configuration);

    // Add Bot Detection - the core feature of this gateway!
    // Uses appsettings.json "BotDetection" section automatically
    builder.Services.AddBotDetection();

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

    // Routing must be enabled for Bot Detection middleware to resolve endpoints
    app.UseRouting();

    // Admin secret middleware (if configured)
    app.UseAdminSecretMiddleware();

    // Bot Detection middleware - runs on every request
    app.UseBotDetection();

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
