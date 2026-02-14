using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.HttpOverrides;
using Mostlylucid.BotDetection.ClientSide;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.PostgreSQL.Extensions;
using Mostlylucid.GeoDetection.Extensions;
using Mostlylucid.GeoDetection.Models;
using Stylobot.Website.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration is loaded automatically from:
// - appsettings.json
// - appsettings.{Environment}.json
// - User secrets (Development only)
// - Environment variables
var config = builder.Configuration;

// Forward headers from reverse proxy (Caddy) so ASP.NET sees the real client IP.
// Without this, token IP-binding fails because page render sees container IP
// while fingerprint POST sees the real client IP from X-Forwarded-For.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    var trustAllProxies = builder.Environment.IsDevelopment() ||
                          config.GetValue("Network:TrustAllForwardedProxies", false) ||
                          bool.TryParse(Environment.GetEnvironmentVariable("TRUST_ALL_FORWARDED_PROXIES"), out var trustAllFromEnv) &&
                          trustAllFromEnv;

    if (trustAllProxies)
    {
        // Development convenience only: trust all forwarded headers.
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        return;
    }

    // Production-safe defaults: trust only explicitly configured proxies/networks.
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();

    var knownProxyList = config["Network:KnownProxies"] ??
                         Environment.GetEnvironmentVariable("KNOWN_PROXIES") ??
                         string.Empty;
    foreach (var proxy in knownProxyList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        if (IPAddress.TryParse(proxy, out var ip))
            options.KnownProxies.Add(ip);

    var knownNetworkList = config["Network:KnownNetworks"] ??
                           Environment.GetEnvironmentVariable("KNOWN_NETWORKS") ??
                           string.Empty;
    foreach (var network in knownNetworkList.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        var parts = network.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != 2) continue;
        if (!IPAddress.TryParse(parts[0], out var prefix)) continue;
        if (!int.TryParse(parts[1], out var prefixLength)) continue;
        options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLength));
    }
});

builder.Services.AddHealthChecks();

// Add GeoDetection services (full pipeline)
var geoProviderConfig = config.GetValue<string>("GeoLite2:Provider") ?? "IpApi";
var geoProvider = Enum.TryParse<GeoProvider>(geoProviderConfig, true, out var parsedProvider)
    ? parsedProvider
    : GeoProvider.IpApi;

builder.Services.AddGeoRouting(
    configureRouting: options =>
    {
        options.Enabled = true;
        options.AddCountryHeader = true;
        options.StoreInContext = true;
        options.EnableTestMode = builder.Environment.IsDevelopment();
    },
    configureProvider: options =>
    {
        options.Provider = geoProvider;
        options.DatabasePath = config["GeoLite2:DatabasePath"] ?? "data/GeoLite2-City.mmdb";
        options.FallbackToSimple = true;
        options.EnableAutoUpdate = config.GetValue("GeoLite2:EnableAutoUpdate", true);
        options.DownloadOnStartup = config.GetValue("GeoLite2:DownloadOnStartup", true);
        options.LicenseKey = config["GeoLite2:LicenseKey"];
        if (int.TryParse(config["GeoLite2:AccountId"], out var accountId))
            options.AccountId = accountId;
    });

// HttpContextAccessor for tag helpers
builder.Services.AddHttpContextAccessor();

// Helper to get config value with fallback to env var then default
string GetConfig(string configKey, string? envKey, string defaultValue) =>
    config[configKey] ?? (envKey != null ? Environment.GetEnvironmentVariable(envKey) : null) ?? defaultValue;
bool GetConfigBool(string configKey, string? envKey, bool defaultValue) =>
    bool.TryParse(config[configKey] ?? (envKey != null ? Environment.GetEnvironmentVariable(envKey) : null), out var v) ? v : defaultValue;
double GetConfigDouble(string configKey, string? envKey, double defaultValue) =>
    double.TryParse(config[configKey] ?? (envKey != null ? Environment.GetEnvironmentVariable(envKey) : null), out var v) ? v : defaultValue;
int GetConfigInt(string configKey, string? envKey, int defaultValue) =>
    int.TryParse(config[configKey] ?? (envKey != null ? Environment.GetEnvironmentVariable(envKey) : null), out var v) ? v : defaultValue;

// Add Bot Detection services with configuration (user secrets / appsettings / env vars)
builder.Services.AddBotDetection(options =>
{
    // Trust upstream detection from YARP gateway (skip re-running the full pipeline)
    options.TrustUpstreamDetection = GetConfigBool("BotDetection:TrustUpstreamDetection", "BOTDETECTION_TRUST_UPSTREAM", false);

    // Detection thresholds
    options.BotThreshold = GetConfigDouble("BotDetection:Threshold", "BOTDETECTION_THRESHOLD", 0.7);
    options.BlockDetectedBots = GetConfigBool("BotDetection:BlockDetectedBots", "BOTDETECTION_BLOCK_BOTS", false);
    options.LogDetailedReasons = GetConfigBool("BotDetection:LogDetailedReasons", "BOTDETECTION_LOG_DETAILED", true);
    options.LogAllRequests = GetConfigBool("BotDetection:LogAllRequests", "BOTDETECTION_LOG_ALL_REQUESTS", true);

    // Response headers
    options.ResponseHeaders.Enabled = GetConfigBool("BotDetection:ResponseHeaders:Enabled", "BOTDETECTION_HEADERS_ENABLED", true);
    options.ResponseHeaders.IncludeConfidence = GetConfigBool("BotDetection:ResponseHeaders:IncludeConfidence", "BOTDETECTION_HEADERS_CONFIDENCE", true);
    options.ResponseHeaders.IncludeDetectors = GetConfigBool("BotDetection:ResponseHeaders:IncludeDetectors", "BOTDETECTION_HEADERS_DETECTORS", true);
    options.ResponseHeaders.IncludeProcessingTime = GetConfigBool("BotDetection:ResponseHeaders:IncludeProcessingTime", "BOTDETECTION_HEADERS_PROCESSING_TIME", true);

    // Client-side fingerprinting
    options.ClientSide.Enabled = GetConfigBool("BotDetection:ClientSide:Enabled", "BOTDETECTION_CLIENTSIDE_ENABLED", true);
    options.ClientSide.CollectCanvas = GetConfigBool("BotDetection:ClientSide:CollectCanvas", "BOTDETECTION_CLIENTSIDE_CANVAS", true);
    options.ClientSide.CollectWebGL = GetConfigBool("BotDetection:ClientSide:CollectWebGL", "BOTDETECTION_CLIENTSIDE_WEBGL", true);
    options.ClientSide.CollectAudio = GetConfigBool("BotDetection:ClientSide:CollectAudio", "BOTDETECTION_CLIENTSIDE_AUDIO", true);

    var tokenSecret = GetConfig("BotDetection:ClientSide:TokenSecret", "BOTDETECTION_CLIENTSIDE_TOKEN_SECRET", "");
    if (!string.IsNullOrEmpty(tokenSecret))
        options.ClientSide.TokenSecret = tokenSecret;

    // Signature hash key (for zero-PII hashing)
    var signatureKey = GetConfig("BotDetection:SignatureHashKey", "BOTDETECTION_SIGNATURE_HASH_KEY", "");
    if (!string.IsNullOrEmpty(signatureKey))
        options.SignatureHashKey = signatureKey;

    // AI/LLM Detection Configuration
    // Supports: Heuristic (fast, no external deps) or Ollama (local LLM)
    var aiProvider = GetConfig("BotDetection:AiProvider", "BOTDETECTION_AI_PROVIDER", "Heuristic");
    options.EnableLlmDetection = aiProvider.Equals("Ollama", StringComparison.OrdinalIgnoreCase);

    // Always configure Ollama endpoint - used by DetectionDescriptionService
    // even when detection mode is Heuristic (descriptions are separate from detection)
    options.AiDetection.Ollama.Endpoint = GetConfig("BotDetection:Ollama:Endpoint", "BOTDETECTION_OLLAMA_ENDPOINT", "");
    options.AiDetection.Ollama.Model = GetConfig("BotDetection:Ollama:Model", "BOTDETECTION_OLLAMA_MODEL", "llama3.2:1b");
    options.AiDetection.TimeoutMs = GetConfigInt("BotDetection:Ollama:TimeoutMs", "BOTDETECTION_OLLAMA_TIMEOUT_MS", 5000);

    if (options.EnableLlmDetection)
    {
        options.AiDetection.Provider = Mostlylucid.BotDetection.Models.AiProvider.Ollama;
    }
    else
    {
        // Heuristic mode - fast, no external dependencies
        options.AiDetection.Provider = Mostlylucid.BotDetection.Models.AiProvider.Heuristic;
    }
});

// Add Bot Detection Dashboard UI
builder.Services.AddStyloBotDashboard(options =>
{
    options.BasePath = GetConfig("StyloBotDashboard:BasePath", "STYLOBOT_DASHBOARD_PATH", "/_stylobot");
    options.HubPath = GetConfig("StyloBotDashboard:BasePath", "STYLOBOT_DASHBOARD_PATH", "/_stylobot") + "/hub"; // SignalR hub path
    options.MaxEventsInMemory = GetConfigInt("StyloBotDashboard:MaxEventsInMemory", "STYLOBOT_DASHBOARD_MAX_EVENTS", 1000);
    options.EnableSimulator = false; // REAL detections only - no simulator

    var dashboardPublic = GetConfigBool("StyloBotDashboard:Public", "STYLOBOT_DASHBOARD_PUBLIC", builder.Environment.IsDevelopment());
    var dashboardSecret = GetConfig("StyloBotDashboard:AccessSecret", "STYLOBOT_DASHBOARD_SECRET", "");

    if (!dashboardPublic && string.IsNullOrWhiteSpace(dashboardSecret) && !builder.Environment.IsDevelopment())
    {
        throw new InvalidOperationException(
            "Dashboard access is locked by default in Production. " +
            "Set STYLOBOT_DASHBOARD_SECRET (preferred) or STYLOBOT_DASHBOARD_PUBLIC=true.");
    }

    options.AuthorizationFilter = context =>
    {
        if (dashboardPublic) return Task.FromResult(true);

        if (!string.IsNullOrWhiteSpace(dashboardSecret) &&
            context.Request.Headers.TryGetValue("X-StyloBot-Secret", out var providedSecret) &&
            SecureEquals(providedSecret.ToString(), dashboardSecret))
            return Task.FromResult(true);

        if (builder.Environment.IsDevelopment() &&
            context.Connection.RemoteIpAddress is { } remoteIp &&
            IPAddress.IsLoopback(remoteIp))
            return Task.FromResult(true);

        return Task.FromResult(false);
    };
});

// Add PostgreSQL/TimescaleDB storage for signature ledger and detection events
// This replaces in-memory storage with durable database-backed storage
var pgConnectionString = GetConfig("StyloBotDashboard:PostgreSQL:ConnectionString", "STYLOBOT_POSTGRESQL_CONNECTION", "");
if (string.IsNullOrEmpty(pgConnectionString))
    pgConnectionString = Environment.GetEnvironmentVariable("DATABASE_CONNECTION_STRING") ?? "";

if (string.IsNullOrEmpty(pgConnectionString) && !builder.Environment.IsDevelopment())
{
    throw new InvalidOperationException(
        "PostgreSQL connection string is required in Production. " +
        "Set StyloBotDashboard__PostgreSQL__ConnectionString or DATABASE_CONNECTION_STRING. " +
        "In-memory storage is only allowed in Development.");
}

if (!string.IsNullOrEmpty(pgConnectionString))
{
    builder.Services.AddStyloBotPostgreSQL(pgConnectionString, options =>
    {
        options.EnableTimescaleDB = GetConfigBool("StyloBotDashboard:PostgreSQL:EnableTimescaleDB", "DATABASE_TIMESCALEDB_ENABLED", true);
        options.AutoInitializeSchema = GetConfigBool("StyloBotDashboard:PostgreSQL:AutoInitializeSchema", "DATABASE_AUTO_INIT_SCHEMA", true);
        options.RetentionDays = GetConfigInt("StyloBotDashboard:PostgreSQL:RetentionDays", "DATABASE_RETENTION_DAYS", 30);
        options.EnableAutomaticCleanup = GetConfigBool("StyloBotDashboard:PostgreSQL:EnableAutomaticCleanup", "STYLOBOT_ENABLE_CLEANUP", true);
        options.EnablePgVector = GetConfigBool("StyloBotDashboard:PostgreSQL:EnablePgVector", "STYLOBOT_ENABLE_PGVECTOR", false);
    });
}

// Add services to the container.
builder.Services.AddControllersWithViews();

// Register SEO service
builder.Services.AddSingleton<SeoService>();
builder.Services.AddSingleton<IMarkdownDocsService, MarkdownDocsService>();

var app = builder.Build();

// Start Vite watch in development
Process? viteProcess = null;
if (app.Environment.IsDevelopment())
{
    try
    {
        viteProcess = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "npm",
                Arguments = "run watch",
                WorkingDirectory = Path.GetDirectoryName(typeof(Program).Assembly.Location)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        viteProcess.Start();
        app.Logger.LogInformation("Vite watch mode started");

        // Cleanup on shutdown
        app.Lifetime.ApplicationStopping.Register(() =>
        {
            if (viteProcess != null && !viteProcess.HasExited)
            {
                viteProcess.Kill(true);
                viteProcess.Dispose();
            }
        });
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Failed to start Vite watch mode. Run 'npm run watch' manually.");
    }
}

// Must be first — rewrites RemoteIpAddress from X-Forwarded-For before
// any middleware (GeoRouting, BotDetection) reads the client IP.
app.UseForwardedHeaders();

app.UseHealthChecks("/health");

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.Use(async (context, next) =>
{
    var nonceBytes = RandomNumberGenerator.GetBytes(16);
    var cspNonce = Convert.ToBase64String(nonceBytes);
    context.Items["CspNonce"] = cspNonce;

    var scriptSrc = $"'self' 'nonce-{cspNonce}' 'unsafe-eval' https://umami.mostlylucid.net https://cdn.jsdelivr.net https://cdnjs.cloudflare.com https://unpkg.com";
    var styleSrc = "'self' 'unsafe-inline' https://fonts.googleapis.com https://unpkg.com https://cdn.jsdelivr.net https://cdnjs.cloudflare.com";
    var csp = string.Join("; ", new[]
    {
        "default-src 'self'",
        "base-uri 'self'",
        "frame-ancestors 'none'",
        "object-src 'none'",
        "img-src 'self' data: https:",
        "font-src 'self' data: https://fonts.gstatic.com https://unpkg.com",
        $"style-src {styleSrc}",
        $"script-src {scriptSrc}",
        "connect-src 'self' https://umami.mostlylucid.net ws: wss:",
        "form-action 'self' https://api.web3forms.com"
    });

    context.Response.Headers.TryAdd("Content-Security-Policy", csp);
    context.Response.Headers.TryAdd("X-Content-Type-Options", "nosniff");
    context.Response.Headers.TryAdd("X-Frame-Options", "DENY");
    context.Response.Headers.TryAdd("Referrer-Policy", "strict-origin-when-cross-origin");
    context.Response.Headers.TryAdd("Permissions-Policy", "geolocation=(), microphone=(), camera=()");
    context.Response.Headers.TryAdd("Cross-Origin-Opener-Policy", "same-origin");
    context.Response.Headers.TryAdd("Cross-Origin-Resource-Policy", "same-origin");
    await next();
});

app.UseRouting();

// GeoDetection middleware
app.UseGeoRouting();

// Bot Detection middleware - analyzes all requests
app.UseBotDetection();

// Bot Detection Dashboard - live UI at /_stylobot
app.UseStyloBotDashboard();

app.UseAuthorization();

app.MapStaticAssets();

app.MapBotDetectionFingerprintEndpoint();

var exposeDiagnostics = GetConfigBool("StyloBot:ExposeDiagnostics", "STYLOBOT_EXPOSE_DIAGNOSTICS", builder.Environment.IsDevelopment());
if (exposeDiagnostics)
{
    app.MapBotDetectionEndpoints("/bot-detection");
}

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

static bool SecureEquals(string left, string right)
{
    var leftBytes = Encoding.UTF8.GetBytes(left);
    var rightBytes = Encoding.UTF8.GetBytes(right);
    return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
}
