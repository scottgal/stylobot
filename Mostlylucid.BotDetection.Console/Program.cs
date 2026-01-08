using System.Text.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Mostlylucid.BotDetection.Console.Helpers;
using Mostlylucid.BotDetection.Console.Logging;
using Mostlylucid.BotDetection.Console.Models;
using Mostlylucid.BotDetection.Console.Services;
using Mostlylucid.BotDetection.Console.Transforms;
using Mostlylucid.BotDetection.Events;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.Middleware;
using Serilog;
using Serilog.Events;
using SQLitePCL;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

// Initialize SQLite bundle BEFORE anything else
Batteries.Init();

// Parse command-line arguments
var cmdArgs = Environment.GetCommandLineArgs();
var upstream = GetArg(cmdArgs, "--upstream") ??
               Environment.GetEnvironmentVariable("UPSTREAM") ?? "http://localhost:8080";
var port = GetArg(cmdArgs, "--port") ?? Environment.GetEnvironmentVariable("PORT") ?? "5000";
var mode = GetArg(cmdArgs, "--mode") ?? Environment.GetEnvironmentVariable("MODE") ?? "demo";

// Configure Serilog (console + file logging for errors/warnings only)
// File logging can be configured via appsettings.json Serilog section
var logsDir = Path.Combine(AppContext.BaseDirectory, "logs");
Directory.CreateDirectory(logsDir);

// Build initial configuration from code (will be enriched by appsettings.json)
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Yarp", LogEventLevel.Information)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Mode", mode)
    .Enrich.WithProperty("Port", port)
    .WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        restrictedToMinimumLevel: LogEventLevel.Debug)
    .WriteTo.File(
        Path.Combine(logsDir, "errors-.log"),
        rollingInterval: RollingInterval.Day,
        retainedFileCountLimit: 30,
        outputTemplate:
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}",
        restrictedToMinimumLevel: LogEventLevel.Warning,
        flushToDiskInterval: TimeSpan.FromSeconds(1));

// Read configuration from appsettings.json if available
var configBuilder = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json", true, false);

var tempConfig = configBuilder.Build();
if (tempConfig.GetSection("Serilog").Exists()) logConfig = logConfig.ReadFrom.Configuration(tempConfig);

Log.Logger = logConfig.CreateLogger();

Log.Information("Logging initialized. Logs directory: {LogsDir}", logsDir);
Log.Information("  - File logging: Warning+ only (configure via appsettings.json Serilog section)");

// Add global unhandled exception handlers to catch silent failures
AppDomain.CurrentDomain.UnhandledException += (sender, e) =>
{
    var exception = e.ExceptionObject as Exception;
    Log.Fatal(exception, "UNHANDLED EXCEPTION in AppDomain - IsTerminating: {IsTerminating}", e.IsTerminating);
    Log.CloseAndFlush();
};

TaskScheduler.UnobservedTaskException += (sender, e) =>
{
    Log.Fatal(e.Exception,
        "UNOBSERVED TASK EXCEPTION - TERMINATING PROCESS (this should never happen - indicates a critical bug)");
    // DO NOT call e.SetObserved() - let the process crash
    // The service manager (systemd/Windows Service) will restart it
    // This forces investigation and prevents zombie state where app appears healthy but is broken
};

try
{
    Log.Information("╔══════════════════════════════════════════════════════════╗");
    Log.Information("║   Mostlylucid Bot Detection Console Gateway            ║");
    Log.Information("╚══════════════════════════════════════════════════════════╝");
    Log.Information("");
    Log.Information("Mode:     {Mode}", mode.ToUpper());
    Log.Information("Upstream: {Upstream}", upstream);
    Log.Information("Port:     {Port}", port);
    Log.Information("");

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args
    });

    // Use Serilog
    builder.Host.UseSerilog();

    // Enable Windows Service support (AOT-compatible)
    builder.Host.UseWindowsService();
    Log.Information("Windows Service support enabled (if running as service)");

    // Configure forwarded headers to extract real client IP from Cloudflare/proxies
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

        // Trust all proxies (Cloudflare, reverse proxies, etc.)
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        // Limit to first proxy for security
        options.ForwardLimit = 1;
    });

    // Load configuration from appsettings.json (with mode override)
    builder.Configuration.AddJsonFile("appsettings.json", false, true);
    builder.Configuration.AddJsonFile($"appsettings.{mode}.json", true, true);

    // Read signature logging configuration early (needed by YARP transforms)
    // DEMO MODE: Enable PII logging by default for debugging (can be disabled in appsettings.json)
    // PRODUCTION MODE: PII logging disabled by default (zero-PII)
    var defaultLogPii = mode.Equals("demo", StringComparison.OrdinalIgnoreCase);

    var sigLoggingConfig = new SignatureLoggingConfig
    {
        Enabled = builder.Configuration.GetValue("SignatureLogging:Enabled", true),
        MinConfidence = builder.Configuration.GetValue("SignatureLogging:MinConfidence", 0.7),
        PrettyPrintJsonLd = builder.Configuration.GetValue("SignatureLogging:PrettyPrintJsonLd", false),
        SignatureHashKey = builder.Configuration.GetValue<string>("SignatureLogging:SignatureHashKey") ??
                           "DEFAULT_INSECURE_KEY_CHANGE_ME",
        LogRawPii = builder.Configuration.GetValue("SignatureLogging:LogRawPii",
            defaultLogPii) // Demo: true, Production: false
    };

    // Validate HMAC key (fail-fast on default key in production)
    ConfigValidator.ValidateHmacKey(sigLoggingConfig, mode);

    // Create signature logger with async background queue
    var signatureLogger = new SignatureLogger();
    builder.Services.AddSingleton(signatureLogger);

    // Create YARP transforms
    var requestTransform = new BotDetectionRequestTransform(mode, sigLoggingConfig, signatureLogger);
    var responseTransform = new BotDetectionResponseTransform(mode);

    // Add YARP
    var yarpBuilder = builder.Services.AddReverseProxy()
        .LoadFromMemory(
            new[]
            {
                new RouteConfig
                {
                    RouteId = "catch-all",
                    Match = new RouteMatch
                    {
                        Path = "{**catch-all}"
                    },
                    ClusterId = "upstream"
                }
            },
            new[]
            {
                new ClusterConfig
                {
                    ClusterId = "upstream",
                    Destinations = new Dictionary<string, DestinationConfig>
                    {
                        ["default"] = new() { Address = upstream }
                    }
                }
            });

    // Add Bot Detection (configured via appsettings.json)
    builder.Services.AddBotDetection();

    // Add heartbeat service to detect silent failures (logs every 5 minutes)
    builder.Services.AddHostedService<HeartbeatService>();

    // Add YARP transforms for bot detection headers and CSP fixes
    yarpBuilder.AddTransforms(builderContext =>
    {
        builderContext.AddRequestTransform(async transformContext =>
            await requestTransform.TransformAsync(transformContext));

        builderContext.AddResponseTransform(async transformContext =>
            await responseTransform.TransformAsync(transformContext));
    });

    var app = builder.Build();

    // Load signatures from JSON-L files on startup
    await SignatureLoaderService.LoadSignaturesFromJsonL(app.Services, Log.Logger);

    // Use Forwarded Headers middleware FIRST to extract real client IP
    app.UseForwardedHeaders();

    // Use Bot Detection middleware
    app.UseBotDetection();

    // Health check endpoint (AOT-compatible) - mapped BEFORE YARP to avoid being proxied
    app.MapGet("/health",
        () => Results.Text(
            $"{{\"status\":\"healthy\",\"mode\":\"{mode}\",\"upstream\":\"{upstream}\",\"port\":\"{port}\"}}",
            "application/json"));

    // Serve embedded test page - mapped BEFORE YARP to avoid being proxied
    app.MapGet("/test-client-side.html", async (HttpContext context) =>
    {
        var assembly = typeof(Program).Assembly;
        var resourceName = "wwwroot.test-client-side.html";

        await using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return Results.NotFound($"Embedded resource '{resourceName}' not found");

        using var reader = new StreamReader(stream);
        var content = await reader.ReadToEndAsync();
        return Results.Content(content, "text/html");
    });

    // Learning endpoint - Active in demo and learning modes (demo is default)
    // MUST be mapped BEFORE YARP to avoid being proxied
    if (mode.Equals("demo", StringComparison.OrdinalIgnoreCase) ||
        mode.Equals("learning", StringComparison.OrdinalIgnoreCase))
    {
        Log.Information("Signature learning endpoint enabled - /stylobot-learning/ active (mode: {Mode})", mode);

        // Supports status code simulation via path markers: /404/, /403/, /500/, etc.
        // Example: /stylobot-learning/404/admin.php -> returns 404
        // Example: /stylobot-learning/products -> returns 200
        app.MapMethods("/stylobot-learning/{**path}", new[] { "GET", "POST", "HEAD", "PUT", "DELETE", "PATCH" },
            (HttpContext context) =>
            {
                // Use actual request path instead of route values to avoid double prefix
                var requestPath = context.Request.Path.Value ?? "/";
                // Remove /stylobot-learning prefix and normalize
                var path = requestPath.StartsWith("/stylobot-learning/", StringComparison.OrdinalIgnoreCase)
                    ? requestPath.Substring("/stylobot-learning/".Length).Trim('/')
                    : requestPath.StartsWith("/stylobot-learning", StringComparison.OrdinalIgnoreCase)
                        ? requestPath.Substring("/stylobot-learning".Length).Trim('/')
                        : "";

                var method = context.Request.Method;
                var userAgent = context.Request.Headers.UserAgent.ToString();

                // Determine status code from path markers
                var statusCode = 200;
                var statusReason = "OK";

                if (path.Contains("/404/") || path.EndsWith(".php") || path.Contains("admin") || path.Contains("wp-"))
                {
                    statusCode = 404;
                    statusReason = "Not Found";
                }
                else if (path.Contains("/403/") || path.Contains("forbidden"))
                {
                    statusCode = 403;
                    statusReason = "Forbidden";
                }
                else if (path.Contains("/500/") || path.Contains("error"))
                {
                    statusCode = 500;
                    statusReason = "Internal Server Error";
                }

                // Build normalized URL path (avoid double slashes)
                var urlPath = string.IsNullOrEmpty(path) ? "/stylobot-learning" : $"/stylobot-learning/{path}";

                Log.Information(
                    "[LEARNING-MODE] Request handled internally: {Method} {UrlPath} UA={UserAgent} -> {StatusCode}",
                    method, urlPath, userAgent.Length > 50 ? userAgent.Substring(0, 47) + "..." : userAgent,
                    statusCode);

                // Return appropriate response based on status code
                var responseJson = statusCode == 404
                    ? $$"""
                        {
                          "@context": "https://schema.org",
                          "@type": "WebPage",
                          "name": "404 Not Found",
                          "description": "The requested resource was not found.",
                          "url": "{{urlPath}}",
                          "metadata": {
                            "statusCode": 404,
                            "statusText": "Not Found",
                            "learningMode": true
                          }
                        }
                        """
                    : $$"""
                        {
                          "@context": "https://schema.org",
                          "@type": "WebPage",
                          "name": "Stylobot Learning Mode",
                          "url": "{{urlPath}}",
                          "description": "This is a synthetic response for bot detection training. No real website was contacted.",
                          "provider": {
                            "@type": "Organization",
                            "name": "Stylobot Bot Detection",
                            "url": "https://stylobot.net"
                          },
                          "mainEntity": {
                            "@type": "Dataset",
                            "name": "Training Data",
                            "description": "Request processed for bot detection learning",
                            "temporalCoverage": "{{DateTime.UtcNow:O}}",
                            "distribution": {
                              "@type": "DataDownload",
                              "contentUrl": "{{urlPath}}",
                              "encodingFormat": "application/json"
                            }
                          },
                          "metadata": {
                            "requestMethod": "{{method}}",
                            "requestPath": "{{urlPath}}",
                            "statusCode": {{statusCode}},
                            "statusText": "{{statusReason}}",
                            "detectionApplied": true,
                            "learningMode": true
                          }
                        }
                        """;

                context.Response.StatusCode = statusCode;
                return Results.Content(responseJson, "application/json");
            });
    }

    // Client-side detection callback endpoint (AOT-compatible)
    app.MapPost("/api/bot-detection/client-result", async (HttpContext context, ILearningEventBus? eventBus) =>
    {
        try
        {
            using var reader = new StreamReader(context.Request.Body);
            var body = await reader.ReadToEndAsync();

            Log.Information("[CLIENT-SIDE-CALLBACK] Received client-side detection result");

            // Parse JSON (AOT-compatible using JsonDocument)
            using var jsonDoc = JsonDocument.Parse(body);
            var root = jsonDoc.RootElement;

            // Extract server detection results (echoed back from client)
            var serverDetection = root.TryGetProperty("serverDetection", out var serverDet)
                ? serverDet
                : (JsonElement?)null;
            var serverIsBot = serverDetection?.TryGetProperty("isBot", out var isBotProp) == true &&
                              isBotProp.GetString() == "True";
            var serverProbability = serverDetection?.TryGetProperty("probability", out var probProp) == true
                ? double.Parse(probProp.GetString() ?? "0")
                : 0.0;

            // Extract client-side checks
            var clientChecks = root.TryGetProperty("clientChecks", out var checks) ? checks : (JsonElement?)null;
            if (clientChecks.HasValue)
            {
                var hasCanvas = clientChecks.Value.TryGetProperty("hasCanvas", out var canvas) && canvas.GetBoolean();
                var hasWebGL = clientChecks.Value.TryGetProperty("hasWebGL", out var webgl) && webgl.GetBoolean();
                var hasAudioContext = clientChecks.Value.TryGetProperty("hasAudioContext", out var audio) &&
                                      audio.GetBoolean();
                var pluginCount = clientChecks.Value.TryGetProperty("pluginCount", out var plugins)
                    ? plugins.GetInt32()
                    : 0;
                var hardwareConcurrency = clientChecks.Value.TryGetProperty("hardwareConcurrency", out var hardware)
                    ? hardware.GetInt32()
                    : 0;

                // Calculate client-side "bot score" based on checks
                var clientBotScore = CalculateClientBotScore(hasCanvas, hasWebGL, hasAudioContext, pluginCount,
                    hardwareConcurrency);

                Log.Information(
                    "[CLIENT-SIDE-VALIDATION] Server: IsBot={ServerIsBot} (prob={ServerProb:F2}), Client: Score={ClientScore:F2}",
                    serverIsBot, serverProbability, clientBotScore);

                // Detect mismatches (server says bot, but client looks human - or vice versa)
                var mismatch = (serverIsBot && clientBotScore < 0.3) || (!serverIsBot && clientBotScore > 0.7);
                if (mismatch)
                    Log.Warning(
                        "[CLIENT-SIDE-MISMATCH] Server detection ({ServerIsBot}) conflicts with client score ({ClientScore:F2})",
                        serverIsBot, clientBotScore);

                // Publish learning event for pattern improvement
                if (eventBus != null)
                {
                    var learningEvent = new LearningEvent
                    {
                        Type = LearningEventType.ClientSideValidation,
                        Source = "ClientSideCallback",
                        Timestamp = DateTimeOffset.UtcNow,
                        Label = serverIsBot, // Server's verdict
                        Confidence = clientBotScore, // Client-side bot score
                        Metadata = new Dictionary<string, object>
                        {
                            ["ipAddress"] = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                            ["userAgent"] = root.TryGetProperty("userAgent", out var ua) ? ua.GetString() ?? "" : "",
                            ["serverIsBot"] = serverIsBot,
                            ["serverProbability"] = serverProbability,
                            ["clientBotScore"] = clientBotScore,
                            ["hasCanvas"] = hasCanvas,
                            ["hasWebGL"] = hasWebGL,
                            ["hasAudioContext"] = hasAudioContext,
                            ["pluginCount"] = pluginCount,
                            ["hardwareConcurrency"] = hardwareConcurrency,
                            ["mismatch"] = mismatch
                        }
                    };

                    if (eventBus.TryPublish(learningEvent))
                        Log.Debug("[CLIENT-SIDE-CALLBACK] Published learning event for client-side validation");
                    else
                        Log.Warning("[CLIENT-SIDE-CALLBACK] Failed to publish learning event (channel full?)");
                }
            }

            return Results.Text("{\"status\":\"accepted\",\"message\":\"Client-side detection result processed\"}",
                "application/json");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Failed to process client-side detection callback");
            return Results.Text("{\"status\":\"error\",\"message\":\"Invalid request\"}", "application/json",
                statusCode: 400);
        }
    });

    // Map YARP reverse proxy (catch-all, should be LAST)
    app.MapReverseProxy();

    // Configure Kestrel to listen on specified port
    app.Urls.Add($"http://*:{port}");

    Log.Information("✓ Gateway ready on http://localhost:{Port}", port);
    Log.Information("✓ Proxying to {Upstream}", upstream);
    Log.Information("✓ Health check: http://localhost:{Port}/health", port);
    Log.Information("");
    Log.Information("Starting application host... (press Ctrl+C to stop)");

    try
    {
        await app.RunAsync();
        Log.Warning("Application host stopped normally (this should only happen on shutdown)");
    }
    catch (OperationCanceledException)
    {
        Log.Information("Application shutdown requested (Ctrl+C or SIGTERM)");
    }
    catch (Exception innerEx)
    {
        Log.Fatal(innerEx, "Application host crashed with unhandled exception");
        throw;
    }
    finally
    {
        // Flush signature logger before shutdown
        await signatureLogger.FlushAndStopAsync();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup or configuration failed");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// Calculate client-side bot score based on browser fingerprinting checks
static double CalculateClientBotScore(bool hasCanvas, bool hasWebGL, bool hasAudioContext, int pluginCount,
    int hardwareConcurrency)
{
    var score = 0.0;

    // Headless browsers typically fail these checks
    if (!hasCanvas) score += 0.30; // Major red flag
    if (!hasWebGL) score += 0.25; // Very suspicious
    if (!hasAudioContext) score += 0.15; // Somewhat suspicious

    // Real browsers typically have 1-5 plugins (though modern browsers have few)
    if (pluginCount == 0) score += 0.10; // Suspicious but not definitive

    // Headless browsers often report 0 or suspiciously high values
    if (hardwareConcurrency == 0) score += 0.10;
    else if (hardwareConcurrency > 32) score += 0.05; // Unusual but possible

    // If all checks pass, give strong confidence it's a real browser
    if (hasCanvas && hasWebGL && hasAudioContext && hardwareConcurrency > 0 && hardwareConcurrency <= 32)
        score = Math.Max(0, score - 0.20); // Bonus for passing all checks

    return Math.Clamp(score, 0.0, 1.0);
}

// Helper to get command-line argument
static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];

    return null;
}