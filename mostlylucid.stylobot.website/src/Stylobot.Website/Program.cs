using System.Diagnostics;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.PostgreSQL.Extensions;
using Stylobot.Website.Services;

var builder = WebApplication.CreateBuilder(args);

// Configuration is loaded automatically from:
// - appsettings.json
// - appsettings.{Environment}.json
// - User secrets (Development only)
// - Environment variables
var config = builder.Configuration;

builder.Services.AddHealthChecks();

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

    if (options.EnableLlmDetection)
    {
        options.AiDetection.Provider = Mostlylucid.BotDetection.Models.AiProvider.Ollama;
        options.AiDetection.Ollama.Endpoint = GetConfig("BotDetection:Ollama:Endpoint", "BOTDETECTION_OLLAMA_ENDPOINT", "http://localhost:11434");
        options.AiDetection.Ollama.Model = GetConfig("BotDetection:Ollama:Model", "BOTDETECTION_OLLAMA_MODEL", "llama3.2:1b");
        options.AiDetection.TimeoutMs = GetConfigInt("BotDetection:Ollama:TimeoutMs", "BOTDETECTION_OLLAMA_TIMEOUT_MS", 5000);
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
});

// Add PostgreSQL/TimescaleDB storage for signature ledger and detection events
// This replaces in-memory storage with durable database-backed storage
var pgConnectionString = GetConfig("StyloBotDashboard:PostgreSQL:ConnectionString", "STYLOBOT_POSTGRESQL_CONNECTION", "");
if (!string.IsNullOrEmpty(pgConnectionString))
{
    builder.Services.AddStyloBotPostgreSQL(pgConnectionString, options =>
    {
        options.EnableTimescaleDB = GetConfigBool("StyloBotDashboard:PostgreSQL:EnableTimescaleDB", "STYLOBOT_ENABLE_TIMESCALEDB", true);
        options.AutoInitializeSchema = GetConfigBool("StyloBotDashboard:PostgreSQL:AutoInitializeSchema", "STYLOBOT_AUTO_INIT_SCHEMA", true);
        options.RetentionDays = GetConfigInt("StyloBotDashboard:PostgreSQL:RetentionDays", "STYLOBOT_RETENTION_DAYS", 30);
        options.EnableAutomaticCleanup = GetConfigBool("StyloBotDashboard:PostgreSQL:EnableAutomaticCleanup", "STYLOBOT_ENABLE_CLEANUP", true);
        options.EnablePgVector = GetConfigBool("StyloBotDashboard:PostgreSQL:EnablePgVector", "STYLOBOT_ENABLE_PGVECTOR", false); // For embedding similarity
    });
}

// Add services to the container.
builder.Services.AddControllersWithViews();

// Register SEO service
builder.Services.AddSingleton<SeoService>();

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

app.UseHealthChecks("/health");

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

// Bot Detection middleware - analyzes all requests
app.UseBotDetection();

// Bot Detection Dashboard - live UI at /_stylobot
app.UseStyloBotDashboard();

app.UseAuthorization();

app.MapStaticAssets();

app.MapBotDetectionEndpoints("/bot-detection");

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
