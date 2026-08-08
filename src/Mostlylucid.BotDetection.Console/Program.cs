using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Mostlylucid.BotDetection.Console.Helpers;
using Mostlylucid.BotDetection.Console.Logging;
using Mostlylucid.BotDetection.Console.Models;
using Mostlylucid.BotDetection.Console.Services;
using Mostlylucid.BotDetection.Console.Transforms;
using Mostlylucid.BotDetection.Api;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Extensions;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Services.Auth;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Llm.Cloud.Extensions;
using Mostlylucid.BotDetection.Llm.LlamaSharp.Extensions;
using Mostlylucid.BotDetection.Llm.Ollama.Extensions;
using Mostlylucid.BotDetection.Llm.Tunnel.Extensions;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Telemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Serilog;
using Serilog.Events;
using SQLitePCL;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms;

// Initialize SQLite bundle BEFORE anything else
Batteries.Init();

// Parse command-line arguments
var cmdArgs = Environment.GetCommandLineArgs();

// --output-config <filename>: dump the effective BotDetection configuration to
// disk and exit. Aids editing - operators get a fully-populated tree (defaults +
// any overrides from appsettings / env / CLI) instead of having to grep the source.
var outputConfigPath = ConfigOutputCommand.TryGetOutputPath(cmdArgs);
if (outputConfigPath is not null)
{
    return ConfigOutputCommand.WriteConfig(outputConfigPath, cmdArgs);
}

// --output-fetch-sources-doc <filename>: generate Markdown documentation of every
// external fetch source (URL, purpose, licence, cadence) straight from the fetch
// registry and exit. Never hand-maintain this list elsewhere.
var outputFetchSourcesDocPath = FetchSourceDocsCommand.TryGetOutputPath(cmdArgs);
if (outputFetchSourcesDocPath is not null)
{
    return FetchSourceDocsCommand.WriteDoc(outputFetchSourcesDocPath, cmdArgs);
}

// -e / --economy: low-memory, in-memory, no-persistence one-shot toggle.
// Sets STYLOBOT_INMEMORY=1 (Null/InMemory store bindings, no SQLite writes
// for behavioural state) and STYLOBOT_PROFILE=economy (smallest Kestrel
// thread pool / connection cap / buffer set). Aimed at scratch containers,
// Pi-class devices, and serverless cold starts. Strip from args so
// downstream parsers ignore it.
if (cmdArgs.Any(a => a.Equals("-e", StringComparison.OrdinalIgnoreCase)
                  || a.Equals("--economy", StringComparison.OrdinalIgnoreCase)))
{
    Environment.SetEnvironmentVariable("STYLOBOT_INMEMORY", "1");
    Environment.SetEnvironmentVariable("STYLOBOT_PROFILE", "economy");
    cmdArgs = cmdArgs.Where(a =>
        !a.Equals("-e", StringComparison.OrdinalIgnoreCase) &&
        !a.Equals("--economy", StringComparison.OrdinalIgnoreCase)).ToArray();
    args = args.Where(a =>
        !a.Equals("-e", StringComparison.OrdinalIgnoreCase) &&
        !a.Equals("--economy", StringComparison.OrdinalIgnoreCase)).ToArray();
}

// -d / --daemon: shorthand for the `start` subcommand. Forks to background and exits.
// Existing `stylobot start` subcommand still works; -d is the common-case ergonomic
// flag operators expect on a CLI ("run in the background").
if (cmdArgs.Any(a => a.Equals("-d", StringComparison.OrdinalIgnoreCase)
                  || a.Equals("--daemon", StringComparison.OrdinalIgnoreCase)))
{
    return DaemonCommands.Start(cmdArgs.Where(a =>
        !a.Equals("-d", StringComparison.OrdinalIgnoreCase) &&
        !a.Equals("--daemon", StringComparison.OrdinalIgnoreCase)).ToArray());
}

// Route subcommands: stop, status, logs don't need the full server startup
var firstArg = cmdArgs.Length > 1 ? cmdArgs[1].ToLowerInvariant() : null;
switch (firstArg)
{
    case "stop":
        return DaemonCommands.Stop();
    case "status":
        return await DaemonCommands.Status();
    case "logs":
        return DaemonCommands.Logs();
    case "start":
        return DaemonCommands.Start(cmdArgs);
    case "genkey":
        Console.WriteLine(Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)));
        return 0;
    case "clear":
        return await ClearLearnedPatternsAsync(cmdArgs);
    case "archetype-from-bdf":
        return await Mostlylucid.BotDetection.Console.ArchetypeFromBdfCommand.RunAsync(cmdArgs);
    case "man":
        ShowManPage();
        return 0;
    case "llmtunnel":
        return await LlmTunnelCommand.RunAsync(cmdArgs);
    case "dashboard":
        return await RunDashboardAsync(cmdArgs);
    case "setup":
        return await SetupCommand.RunAsync(cmdArgs);
}

// llmtunnel also accepts as a flag form: --llm-tunnel or -llmtunnel
if (cmdArgs.Any(a => a.Equals("-llmtunnel", StringComparison.OrdinalIgnoreCase)
                  || a.Equals("--llm-tunnel", StringComparison.OrdinalIgnoreCase)))
{
    return await LlmTunnelCommand.RunAsync(cmdArgs);
}

// Show help if no args or --help
if (cmdArgs.Length <= 1 || cmdArgs.Contains("--help") || cmdArgs.Contains("-h"))
{
    Console.WriteLine();
    Console.WriteLine("  stylobot · self-hosted bot defense · free forever");
    Console.WriteLine("  https://stylo.bot");
    Console.WriteLine();
    Console.WriteLine("  Usage:");
    Console.WriteLine("    stylobot <port> <upstream>                  Proxy to upstream on port");
    Console.WriteLine("    stylobot --port <port> --upstream <url>    Standard named options");
    Console.WriteLine("    stylobot <port> <upstream> --mode production     Enable blocking");
    Console.WriteLine();
    Console.WriteLine("  Commands:");
    Console.WriteLine("    stylobot start <port> <upstream> [opts]     Start as background daemon");
    Console.WriteLine("    stylobot <port> <upstream> -d              Same, shorter (-d / --daemon)");
    Console.WriteLine("    stylobot <port> <upstream> -e              Economy mode: low memory, no persistence (-e / --economy)");
    Console.WriteLine("    stylobot --output-config <file>            Dump effective config to <file>");
    Console.WriteLine("    stylobot --output-fetch-sources-doc <file> Generate fetch-source docs (URLs/purpose/licence) to <file>");
    Console.WriteLine("    stylobot stop                               Stop the running daemon");
    Console.WriteLine("    stylobot status                             Check if daemon is running");
    Console.WriteLine("    stylobot logs                               Show recent log output");
    Console.WriteLine("    stylobot man                                Full reference manual");
    Console.WriteLine("    stylobot llmtunnel [token] [opts]           Start local LLM agent + Cloudflare tunnel");
    Console.WriteLine("    stylobot dashboard <url> [--api-key <k>]   Remote dashboard for another stylobot instance");
    Console.WriteLine("    stylobot dashboard hash-password           Hash a password for StyloBot:Dashboard:Auth:PasswordHash");
    Console.WriteLine("    stylobot genkey                             Generate a random 32-byte base64 key");
    Console.WriteLine("    stylobot clear [--sessions]                 Clear learned patterns (and optionally sessions)");
    Console.WriteLine("    stylobot setup [--check-only] [--force]     Check and download missing resources");
    Console.WriteLine("    stylobot archetype-from-bdf <dir>          Import BDF fixtures as identity archetypes");
    Console.WriteLine();
    Console.WriteLine("  LLM Tunnel Options:");
    Console.WriteLine("    --ollama <url>              Local Ollama URL (default: http://127.0.0.1:11434)");
    Console.WriteLine("    --models <csv>              Comma-separated model allowlist");
    Console.WriteLine("    --max-concurrency <n>       Max concurrent inference requests (default: 2)");
    Console.WriteLine("    --max-context <tokens>      Max context tokens (default: 8192)");
    Console.WriteLine("    --agent-port <port>         Loopback agent port (default: random)");
    Console.WriteLine();
    Console.WriteLine("  Options:");
    Console.WriteLine("    --port <port>               Port to listen on (default: 5080)");
    Console.WriteLine("    --upstream <url>            Upstream server URL");
    Console.WriteLine("    --mode <demo|production>    Detection mode (default: demo)");
    Console.WriteLine("    --policy <name>             Default action policy");
    Console.WriteLine("                                (default: block in production, logonly in demo)");
    Console.WriteLine("    --cert <path>               TLS certificate (.pfx or .pem)");
    Console.WriteLine("    --key <path>                TLS private key (required with .pem cert)");
    Console.WriteLine("    --cert-password <pass>      PFX certificate password");
    Console.WriteLine("    --tunnel [[token]]            Cloudflare Tunnel (requires cloudflared)");
    Console.WriteLine("    --threshold <0.0-1.0>       Bot probability threshold (default: 0.7)");
    Console.WriteLine("    --llm <provider>            LLM provider (openai, anthropic, gemini, groq,");
    Console.WriteLine("                                mistral, deepseek, ollama, or custom URL)");
    Console.WriteLine("    --llm-key <key>             API key (or env: STYLOBOT_LLM_KEY)");
    Console.WriteLine("    --llm-url <url>             Custom provider base URL");
    Console.WriteLine("    --model <name>              Model name (overrides provider default)");
    Console.WriteLine("    --config <path>             Path to appsettings.json override");
    Console.WriteLine("    --config-dir <path>         Load YAML policy/application files from directory");
    Console.WriteLine("    --log-level <level>         Minimum log level (default: Warning)");
    Console.WriteLine("    --verbose                   Show all log output (disables live table)");
    Console.WriteLine("    --skip-dependencies-check   Skip first-run downloads (bot list, GeoIP,");
    Console.WriteLine("                                Ollama model pull). Use for CI / automated starts.");
    Console.WriteLine("    -e, --economy               Economy mode: in-memory only, no SQLite writes for");
    Console.WriteLine("                                behavioural state, smallest Kestrel profile. For Pi /");
    Console.WriteLine("                                256MB containers / serverless cold starts. Detection");
    Console.WriteLine("                                still runs; you lose learning, entity resolution, the");
    Console.WriteLine("                                metastable identity layer, the verdict cache, and any");
    Console.WriteLine("                                cross-restart history.");
    Console.WriteLine("    --enable-api                Expose /api/v1/* REST endpoints + SignalR hub at");
    Console.WriteLine("                                /api/v1/hub for remote stylobot-ui hosts. Requires");
    Console.WriteLine("                                BotDetection:ApiKeys configured in appsettings.json.");
    Console.WriteLine("    --origin-tunnel <hostname>  Reach private origin through Cloudflare Tunnel B.");
    Console.WriteLine("                                Pairs with --tunnel for full brownfield retrofit.");
    Console.WriteLine("    --profile <name>            Kestrel profile: balanced (default), api, site,");
    Console.WriteLine("                                highrisk, economy. Also settable via STYLOBOT_PROFILE.");
    Console.WriteLine("    --no-deps-check             Alias for --skip-dependencies-check. Skip first-run");
    Console.WriteLine("                                downloads (bot list, GeoIP, Ollama model pull).");
    Console.WriteLine("    -h, --help                  Show this help");
    Console.WriteLine();
    Console.WriteLine("  Examples:");
    Console.WriteLine("    stylobot 5080 http://localhost:3000");
    Console.WriteLine("    stylobot 5080 http://localhost:3000 --mode demo");
    Console.WriteLine("    stylobot 8000 http://192.168.0.6:2040 --mode production");
    Console.WriteLine("    stylobot start 5080 http://localhost:3000 --policy block");
    Console.WriteLine("    stylobot 443 https://api.example.com --cert cert.pfx");
    Console.WriteLine("    stylobot 5080 http://localhost:3000 --tunnel");
    Console.WriteLine("    stylobot 5080 http://localhost:3000 -e        # Pi-class / scratch container");
    Console.WriteLine();
    Console.WriteLine("  Health:     http://localhost:<port>/health");
    Console.WriteLine();
    Console.WriteLine("  Docs:       https://github.com/scottgal/stylobot");
    Console.WriteLine("  Commercial: https://stylo.bot/pricing");
    Console.WriteLine();
    return 0;
}

// Parse: stylobot <port> <upstream> [--mode demo|production]
// Collect positional args, skipping values that belong to known flags
var flagsWithValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    { "--port", "--upstream", "--mode", "--policy", "--cert", "--key", "--cert-password", "--config", "--log-level", "--threshold", "--llm", "--llm-key", "--llm-url", "--model", "--origin-tunnel" };
var positionals = new List<string>();
for (var i = 1; i < cmdArgs.Length; i++)
{
    if (cmdArgs[i].StartsWith("-"))
    {
        if (flagsWithValues.Contains(cmdArgs[i]) && i + 1 < cmdArgs.Length)
            i++; // skip the flag's value
        else if (cmdArgs[i].Equals("--tunnel", StringComparison.OrdinalIgnoreCase)
                 && i + 1 < cmdArgs.Length && !cmdArgs[i + 1].StartsWith("-")
                 && !Uri.TryCreate(cmdArgs[i + 1], UriKind.Absolute, out _)
                 && !int.TryParse(cmdArgs[i + 1], out _))
            i++; // skip tunnel token (not a URL or port)
    }
    else
    {
        positionals.Add(cmdArgs[i]);
    }
}

var cliPort = GetArg(cmdArgs, "--port");
var cliUpstream = GetArg(cmdArgs, "--upstream");
var envPort = Environment.GetEnvironmentVariable("PORT");
var envUpstream = Environment.GetEnvironmentVariable("UPSTREAM")
                  ?? Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM");

string? positionalPort = null;
string? positionalUpstream = null;
if (positionals.Count >= 2)
{
    positionalPort = positionals[0];
    positionalUpstream = positionals[1];
}
else if (positionals.Count == 1)
{
    // Single arg: if it looks like a number, it's the port; otherwise it's the upstream
    if (int.TryParse(positionals[0], out _))
        positionalPort = positionals[0];
    else
        positionalUpstream = positionals[0];
}

var port = cliPort ?? positionalPort ?? envPort ?? "5080";

// --origin-tunnel <private-hostname>: brownfield retrofit (6.8.2+). Stylobot
// reaches the backend through a Cloudflare Tunnel instead of a public hostname
// -- the legacy box has zero public exposure. See docs/brownfield-retrofit.md.
//
// Mechanics: pick a free loopback port, launch `cloudflared access tcp` to
// forward it to the configured private hostname, then rewrite the upstream
// URL to point at that local port. Stylobot is unaware -- it just proxies to
// http://localhost:<auto-port> as if it were any other origin.
//
// Resolved upstream precedence is: explicit --upstream / positional / env > origin-tunnel.
// When the operator supplies both, we honour the explicit upstream and ignore the
// tunnel (and log a warning) so a misconfigured retrofit doesn't silently divert
// production traffic.
var originTunnelHost = GetArg(cmdArgs, "--origin-tunnel");
string? originTunnelEffectiveUpstream = null;
if (!string.IsNullOrWhiteSpace(originTunnelHost))
{
    var hasExplicitUpstream = cliUpstream != null || positionalUpstream != null || envUpstream != null;
    if (hasExplicitUpstream)
    {
        Console.Error.WriteLine(
            $"  Warning: --origin-tunnel '{originTunnelHost}' is set but an explicit upstream was also provided. " +
            "Honouring the explicit upstream; ignoring --origin-tunnel.");
    }
    else
    {
        var tunnelPort = Mostlylucid.BotDetection.Console.Services.OriginTunnelLauncher.PickFreeLoopbackPort();
        originTunnelEffectiveUpstream = $"http://localhost:{tunnelPort}";
        // Process kept alive by stylobot's lifetime; cloudflared exits with the parent.
        _ = Mostlylucid.BotDetection.Console.Services.OriginTunnelLauncher.Launch(originTunnelHost, tunnelPort);
    }
}

var upstream = cliUpstream ?? positionalUpstream ?? originTunnelEffectiveUpstream ?? envUpstream ?? "http://localhost:8080";
// --tunnel implies public traffic → default to production mode so the policy
// stack is active. Operators that want observe-only on a tunnel can still pass
// --mode demo explicitly.
var hasTunnel = cmdArgs.Any(a => a.Equals("--tunnel", StringComparison.OrdinalIgnoreCase));
var modeDefault = hasTunnel ? "production" : "demo";
var mode = GetArg(cmdArgs, "--mode") ?? Environment.GetEnvironmentVariable("MODE") ?? modeDefault;
var isDemoLikeMode = mode.Equals("demo", StringComparison.OrdinalIgnoreCase) ||
                     mode.Equals("learning", StringComparison.OrdinalIgnoreCase);
// production → use the policy stack (no flat override); demo → logonly shadow mode
var defaultActionPolicy = mode.Equals("production", StringComparison.OrdinalIgnoreCase) ? null : "logonly";
var actionPolicy = GetArg(cmdArgs, "--policy") ?? Environment.GetEnvironmentVariable("STYLOBOT_POLICY") ?? defaultActionPolicy;
var certPath = GetArg(cmdArgs, "--cert");
var keyPath = GetArg(cmdArgs, "--key");
var certPassword = GetArg(cmdArgs, "--cert-password");
var configPath = GetArg(cmdArgs, "--config");
var logLevel = GetArg(cmdArgs, "--log-level");
var thresholdArg = GetArg(cmdArgs, "--threshold");
var llmProvider = GetArg(cmdArgs, "--llm");
var llmKey = GetArg(cmdArgs, "--llm-key") ?? Environment.GetEnvironmentVariable("STYLOBOT_LLM_KEY");
var llmUrl = GetArg(cmdArgs, "--llm-url");
var llmModel = GetArg(cmdArgs, "--model");
var useTls = certPath != null;
var verbose = cmdArgs.Contains("--verbose");
double? botThreshold = thresholdArg != null && double.TryParse(thresholdArg, out var t) ? t : null;

if (!int.TryParse(port, out var portNumber) || portNumber is < 1 or > 65535)
{
    Console.Error.WriteLine($"  Invalid port: {port}");
    return 1;
}

if (!Uri.TryCreate(upstream, UriKind.Absolute, out var upstreamUri) ||
    (upstreamUri.Scheme != Uri.UriSchemeHttp && upstreamUri.Scheme != Uri.UriSchemeHttps))
{
    Console.Error.WriteLine($"  Invalid upstream URL: {upstream}");
    return 1;
}

// Cloudflare tunnel: --tunnel (quick) or --tunnel <token> (named)
string? tunnelToken = null;
var tunnelEnabled = false;
var tunnelArgIndex = Array.FindIndex(cmdArgs, a => a.Equals("--tunnel", StringComparison.OrdinalIgnoreCase));
if (tunnelArgIndex >= 0)
{
    tunnelEnabled = true;
    // Next arg is the token if it doesn't start with --
    if (tunnelArgIndex + 1 < cmdArgs.Length && !cmdArgs[tunnelArgIndex + 1].StartsWith("-"))
        tunnelToken = cmdArgs[tunnelArgIndex + 1];
}

// --skip-dependencies-check (or --no-deps-check / --skip-deps): bypass first-run
// downloads (bot list, GeoIP CSV, Ollama pull). Detection runs against whatever
// is already on disk. Intended for CI / automated starts that should not block
// on a slow registry or a multi-MB pull.
var skipDependencyChecks = cmdArgs.Any(a =>
    a.Equals("--skip-dependencies-check", StringComparison.OrdinalIgnoreCase)
    || a.Equals("--no-deps-check", StringComparison.OrdinalIgnoreCase)
    || a.Equals("--skip-deps", StringComparison.OrdinalIgnoreCase));

// --enable-api (default OFF for minimal surface area): when set, exposes the
// /api/v1/* REST endpoints (detect + the full dashboard read surface) so a remote
// stylobot-ui can be pointed at this gateway. Without it the gateway is a pure
// reverse-proxy + detection host with no operational endpoints beyond /health.
// Auth is via X-SB-Api-Key (BotDetection:ApiKeys appsettings section — the same section
// InMemoryApiKeyStore binds from, so the fail-fast below guards the real key source).
var enableApi = cmdArgs.Any(a =>
    a.Equals("--enable-api", StringComparison.OrdinalIgnoreCase));

// Note: there is no --enable-monitoring flag here because the Console binary
// is a reverse-proxy + detection host without a dashboard. The ASP.NET
// monitoring pack lives in projects that host the dashboard (Demo, the
// stylo.bot website, and commercial variant binaries). To turn it on
// there, set StyloBot:Dashboard:MonitoringPack:Enabled = true in appsettings.

// Validate TLS cert exists
if (certPath != null && !File.Exists(certPath))
{
    Console.Error.WriteLine($"  Certificate file not found: {certPath}");
    return 1;
}
if (keyPath != null && !File.Exists(keyPath))
{
    Console.Error.WriteLine($"  Private key file not found: {keyPath}");
    return 1;
}

// Ollama availability + model presence are checked DURING the startup banner phase
// below (see "ollama-model" task), not here. Pulling at the top of Program.cs would
// happen before the banner is alive and the user would stare at a quiet terminal
// for a multi-GB download.

// Parse log level override (default: Warning when live table active, Debug when verbose)
var minLogLevel = verbose ? LogEventLevel.Debug : LogEventLevel.Warning;
if (logLevel != null && Enum.TryParse<LogEventLevel>(logLevel, true, out var parsed))
    minLogLevel = parsed;

// Configure Serilog (console + file logging for errors/warnings only)
// File logging can be configured via appsettings.json Serilog section
var logsDir = Path.Combine(
    Mostlylucid.BotDetection.Models.BotDetectionOptions.ResolveDataDirectory(), "logs");
Directory.CreateDirectory(logsDir);

// Build initial configuration from code (will be enriched by appsettings.json)
var logConfig = new LoggerConfiguration()
    .MinimumLevel.Is(minLogLevel)
    .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
    .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
    .MinimumLevel.Override("Yarp", LogEventLevel.Warning)
    .Enrich.FromLogContext()
    .Enrich.WithProperty("Mode", mode)
    .Enrich.WithProperty("Port", port);

// Only write to console in verbose mode; live table replaces console output otherwise
if (verbose)
{
    logConfig = logConfig.WriteTo.Console(
        outputTemplate: "[{Timestamp:HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}",
        restrictedToMinimumLevel: LogEventLevel.Debug);
}

logConfig = logConfig.WriteTo.File(
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
    .AddJsonFile("appsettings.json", true, false)
    .AddJsonFile($"appsettings.{mode}.json", true, false)
    .AddEnvironmentVariables()
    .AddEnvironmentVariables("STYLOBOT_");
if (configPath != null)
    configBuilder.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);

var tempConfig = configBuilder.Build();
if (tempConfig.GetSection("Serilog").Exists())
{
    try
    {
        // Explicit assembly reference for single-file/AOT compatibility
        var readerOptions = new Serilog.Settings.Configuration.ConfigurationReaderOptions(
            typeof(Serilog.ConsoleLoggerConfigurationExtensions).Assembly,
            typeof(Serilog.FileLoggerConfigurationExtensions).Assembly);
        logConfig = logConfig.ReadFrom.Configuration(tempConfig, readerOptions);
    }
    catch (InvalidOperationException)
    {
        // Single-file publish: Serilog assembly scanning fails, use code-based config only
    }
}

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
    // Internet-facing edge: raise our own soft fd ceiling toward the hard limit so a burst of
    // concurrent connections can't exhaust the default 1024 soft limit and crash the process
    // with EMFILE on accept()/connect() (no managed exception, looks like a hard crash under
    // load). The shipped systemd unit sets LimitNOFILE=65536, but a bare/docker/CI launch
    // inherits the shell default; self-raising makes the binary robust regardless of launcher.
    var fdLimit = Mostlylucid.BotDetection.Runtime.FileDescriptorLimit.RaiseSoftToHard();
    if (fdLimit is { } fd)
        Log.Information("File-descriptor limit (RLIMIT_NOFILE): soft={Soft} hard={Hard}", fd.Soft, fd.Hard);

    if (verbose)
    {
        Log.Information("");
        Log.Information("  ┌─────────────────────────────────────────┐");
        Log.Information("  │  stylobot  ·  self-hosted bot defense   │");
        Log.Information("  │  https://stylo.bot                   │");
        Log.Information("  └─────────────────────────────────────────┘");
        Log.Information("");
        Log.Information("  Mode:     {Mode}", mode.ToUpper());
        Log.Information("  Policy:   {Policy}", actionPolicy);
        Log.Information("  Upstream: {Upstream}", upstream);
        Log.Information("  Port:     {Port}", port);
        if (useTls) Log.Information("  TLS:      {CertPath}", certPath);
        if (tunnelEnabled) Log.Information("  Tunnel:   Cloudflare {TunnelType}", tunnelToken != null ? "(named)" : "(quick)");
        Log.Information("  Docs:     https://github.com/scottgal/stylobot");
        Log.Information("");
    }

    var builder = WebApplication.CreateBuilder(new WebApplicationOptions
    {
        Args = args
    });

    // FOSS hard rule: no runtime options-reload, by any path -- CreateBuilder's OWN
    // default appsettings.json/appsettings.{env}.json sources reload-on-change
    // regardless of the explicit AddJsonFile calls below (those are ADDITIONAL
    // sources, they don't replace the default ones). A config change needs a
    // process restart.
    foreach (var defaultSource in builder.Configuration.Sources.OfType<Microsoft.Extensions.Configuration.Json.JsonConfigurationSource>())
        defaultSource.ReloadOnChange = false;

    // Use Serilog
    builder.Host.UseSerilog();

    // Enable service hosting (Windows SCM + Linux systemd)
    builder.Host.UseWindowsService();
    builder.Host.UseSystemd();

    // Configure forwarded headers to extract real client IP from Cloudflare/proxies
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = 1;

        var trustAllProxies = builder.Configuration.GetValue("Network:TrustAllForwardedProxies", false) ||
                              bool.TryParse(Environment.GetEnvironmentVariable("TRUST_ALL_FORWARDED_PROXIES"), out var trustAll) &&
                              trustAll;

        if (trustAllProxies)
        {
            Log.Warning("TrustAllForwardedProxies is enabled. This allows IP spoofing via X-Forwarded-For. Configure Network:KnownNetworks/KnownProxies instead.");
            options.KnownIPNetworks.Clear();
            options.KnownProxies.Clear();
            return;
        }

        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();

        var configNetworks = builder.Configuration["Network:KnownNetworks"];
        var knownNetworks = string.IsNullOrEmpty(configNetworks)
            ? Environment.GetEnvironmentVariable("KNOWN_NETWORKS") ?? string.Empty
            : configNetworks;
        foreach (var network in knownNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseNetwork(network, out var parsedNetwork))
                options.KnownIPNetworks.Add(parsedNetwork);
            else
                Log.Warning("Ignoring invalid forwarded-header trusted network: {Network}", network);
        }

        var configProxies = builder.Configuration["Network:KnownProxies"];
        var knownProxies = string.IsNullOrEmpty(configProxies)
            ? Environment.GetEnvironmentVariable("KNOWN_PROXIES") ?? string.Empty
            : configProxies;
        foreach (var proxy in knownProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(proxy, out var parsedProxy))
                options.KnownProxies.Add(parsedProxy);
            else
                Log.Warning("Ignoring invalid forwarded-header trusted proxy: {Proxy}", proxy);
        }

        if (tunnelEnabled)
        {
            // Local cloudflared acts as the direct proxy in tunnel mode.
            options.KnownProxies.Add(IPAddress.Loopback);
            options.KnownProxies.Add(IPAddress.IPv6Loopback);
        }
    });

    // Load configuration from appsettings.json (with mode override + CLI config).
    // reloadOnChange: false everywhere -- FOSS hard rule: no runtime options-reload,
    // by any path (not just /admin/reload, which was removed separately). A config
    // change needs a process restart.
    builder.Configuration.AddJsonFile("appsettings.json", true, false);
    builder.Configuration.AddJsonFile($"appsettings.{mode}.json", true, false);
    if (configPath != null)
        builder.Configuration.AddJsonFile(Path.GetFullPath(configPath), optional: false, reloadOnChange: false);
    // Docker/K8s secrets: /run/secrets/<key> files → config keys. Standard in
    // containerized deployments; no-op when the directory doesn't exist.
    builder.Configuration.AddKeyPerFile("/run/secrets", optional: true, reloadOnChange: false);
    builder.Configuration.AddEnvironmentVariables();
    builder.Configuration.AddEnvironmentVariables("STYLOBOT_");

    // Read signature logging configuration early (needed by YARP transforms)
    // DEMO MODE: Enable PII logging by default for debugging (can be disabled in appsettings.json)
    // PRODUCTION MODE: PII logging disabled by default (zero-PII)
    var defaultLogPii = mode.Equals("demo", StringComparison.OrdinalIgnoreCase);

    // Resolve the HMAC key (6.8.2+: auto-generate random in-memory when no
    // key is configured, instead of fail-fast in production). Crashes always
    // trump features -- the gateway must start. Trade-off is documented in
    // ConfigValidator: signatures don't survive a restart when auto-generated.
    var resolvedHmacKey = ConfigValidator.ResolveHmacKey(
        builder.Configuration.GetValue<string>("SignatureLogging:SignatureHashKey"),
        mode);

    var sigLoggingConfig = new SignatureLoggingConfig
    {
        Enabled = builder.Configuration.GetValue("SignatureLogging:Enabled", true),
        MinConfidence = builder.Configuration.GetValue("SignatureLogging:MinConfidence", 0.7),
        PrettyPrintJsonLd = builder.Configuration.GetValue("SignatureLogging:PrettyPrintJsonLd", false),
        SignatureHashKey = resolvedHmacKey,
        LogRawPii = builder.Configuration.GetValue("SignatureLogging:LogRawPii",
            defaultLogPii) // Demo: true, Production: false
    };

    // Create signature logger with async background queue
    var signatureLogger = new SignatureLogger();
    builder.Services.AddSingleton(signatureLogger);

    // Data guardian: bound the signature JSONL footprint. SignatureLogger appends
    // per-day signatures-{date}.jsonl and never prunes; without this a soak grows
    // the on-disk set without limit and stalls boot when the loader scans it. The
    // core GuardianService (registered by AddBotDetection) walks this on its interval.
    // Policy is config-driven (SignatureLogging:Retention:*).
    builder.Services.AddSingleton<Mostlylucid.BotDetection.Guardians.IGuardian>(sp =>
        new SignatureJsonlRetentionGuardian(
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILogger<SignatureJsonlRetentionGuardian>>(),
            directory: ".",
            retentionDays: builder.Configuration.GetValue("SignatureLogging:Retention:Days", 14),
            maxTotalBytes: builder.Configuration.GetValue("SignatureLogging:Retention:MaxTotalMB", 512) * 1024L * 1024L,
            interval: TimeSpan.FromHours(builder.Configuration.GetValue("SignatureLogging:Retention:IntervalHours", 6.0))));

    // Create YARP transforms
    var requestTransform = new BotDetectionRequestTransform(mode, sigLoggingConfig, signatureLogger);
    var responseTransform = new BotDetectionResponseTransform();

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

    // Ephemeral mode: no SQLite, all state lives in-process and evaporates on
    // restart. Opt-in via STYLOBOT_INMEMORY=1 (or "true"/"yes"). Targeted at
    // serverless / scratch containers + demo sandboxes; intentionally weaker
    // than the default persistent mode.
    var inMemoryEnv = Environment.GetEnvironmentVariable("STYLOBOT_INMEMORY");
    var inMemory = inMemoryEnv is not null &&
                   (inMemoryEnv.Equals("1", StringComparison.OrdinalIgnoreCase)
                    || inMemoryEnv.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || inMemoryEnv.Equals("yes", StringComparison.OrdinalIgnoreCase));

    void AddDetection() {
        if (inMemory) builder.Services.AddBotDetectionInMemory();
        else builder.Services.AddBotDetection();
    }

    // Add Bot Detection + optional LLM provider
    if (llmProvider != null)
    {
        // Always register core detection first
        AddDetection();

        if (llmProvider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddStylobotOllama(
                llmUrl ?? LlmDefaults.DefaultEndpoint,
                llmModel ?? LlmDefaults.DefaultModel);
        }
        else if (llmProvider.Equals("localtunnel", StringComparison.OrdinalIgnoreCase))
        {
            builder.Services.AddLocalLlmTunnelClient(opts =>
            {
                if (llmKey != null) opts.ConnectionKey = llmKey;
                if (llmModel != null) opts.DefaultModel = llmModel;
            });
        }
        else
        {
            if (llmProvider.Equals("llamasharp", StringComparison.OrdinalIgnoreCase))
            {
                builder.Services.AddStylobotLlamaSharp(opts =>
                {
                    if (llmModel != null) opts.ModelPath = llmModel;
                });
            }
            else
            {
                builder.Services.AddStylobotCloudLlm(llmProvider, llmKey, llmModel, llmUrl);
            }
        }
    }
    else
    {
        AddDetection();
    }

    // Apply CLI overrides
    builder.Services.PostConfigure<Mostlylucid.BotDetection.Models.BotDetectionOptions>(opts =>
    {
        // null = production mode; let the policy stack handle action routing.
        // non-null = explicit policy override (e.g. logonly in demo mode, or --policy flag).
        if (actionPolicy != null)
            opts.DefaultActionPolicyName = actionPolicy;
#pragma warning disable CS0618 // BotDetectionOptions field deprecated; will be removed in a future major release
        if (botThreshold.HasValue) opts.BotThreshold = botThreshold.Value;
        if (llmProvider != null) opts.EnableLlmDetection = true;
#pragma warning restore CS0618
        // Ensure a writable data directory even when installed to a root-owned path (e.g. apt install)
        opts.DatabasePath ??= Path.Combine(
            Mostlylucid.BotDetection.Models.BotDetectionOptions.ResolveDataDirectory(), "botdetection.db");
    });

    builder.Services.AddBotDetectionTelemetry();

    // Opt-in REST API surface. Off by default - the gateway's day job is detection
    // + proxying; the API is for stylobot-ui hosts that need to read this gateway's
    // detections / signatures / clusters / etc. over HTTP.
    if (enableApi)
    {
        // Fail fast if --enable-api is set but no API keys are configured. Without keys,
        // ApiKeyAuthenticationHandler rejects every request - the API surface would be
        // mapped and unreachable, which is the worst combination (operator thinks it's
        // working until the first real call fails 401).
        //
        // Guard the SAME section InMemoryApiKeyStore binds from: BotDetectionOptions.ApiKeys,
        // which AddBotDetection reads from "BotDetection:ApiKeys". A previous version checked
        // "StyloBot:ApiKeys" -- a section the store never reads -- so a key configured there
        // passed the fail-fast yet left the store empty, producing the exact 401-on-every-call
        // state this guard exists to prevent.
        var apiKeysSection = builder.Configuration.GetSection("BotDetection:ApiKeys");
        if (!apiKeysSection.Exists() || !apiKeysSection.GetChildren().Any())
        {
            Console.Error.WriteLine(
                "FATAL: --enable-api requires at least one entry under BotDetection:ApiKeys " +
                "in appsettings.json (or via env vars / user secrets), e.g. " +
                "BotDetection__ApiKeys__<keyId>__Key=<secret>. Configure a key and restart.");
            return 1;
        }
        builder.Services.AddStyloBotApi(opts => opts.EnableOpenApi = false);

        // Persistence backend: stores detections + serves the REST read endpoints with
        // real data, runs DashboardSummaryBroadcaster, exposes the SignalR hub for live
        // invalidation beacons. Pin the hub path to /api/v1/hub so it sits inside the
        // versioned API namespace rather than at /stylobot/hub.
        builder.Services.AddSingleton(new Mostlylucid.BotDetection.UI.Configuration.StyloBotDashboardOptions
        {
            Enabled = true,
            HubPath = "/api/v1/hub",
            // Gateway auth is via X-SB-Api-Key on the SignalR negotiate request, not
            // dashboard auth; the hub's OnConnectedAsync allows when no other policy is
            // configured (see StyloBotDashboardHub.IsAuthorizedAsync).
            AllowUnauthenticatedAccess = true
        });
        builder.Services.AddBotDetectionPersistence();
    }

    builder.Services.AddOpenTelemetry()
        .WithMetrics(metrics => metrics
            .AddAspNetCoreInstrumentation()
            .AddMeter(BotDetectionMetrics.MeterName)
            .AddMeter(BotDetectionSignalMeter.MeterName)
            .AddPrometheusExporter())
        .WithTracing(tracing => tracing
            .AddAspNetCoreInstrumentation()
            .AddSource(BotDetectionTelemetry.ActivitySourceName));

    // Detection event sink for live table
    var detectionSink = new DetectionEventSink();
    builder.Services.AddSingleton(detectionSink);

    // Add heartbeat service to detect silent failures (logs every 5 minutes).
    // Wave 2 migration: HeartbeatService is no longer a BackgroundService;
    // it subscribes to ScheduleCoordinator.Tick5m. ConsoleHostedSingletonsBootstrap
    // is the eager-resolution shim that fires the singleton's constructor at
    // boot so Subscribe(...) actually runs.
    builder.Services.AddSingleton<HeartbeatService>();
    builder.Services.AddHostedService<ConsoleHostedSingletonsBootstrap>();

    // Add YARP transforms for bot detection headers and CSP fixes
    yarpBuilder.AddTransforms(builderContext =>
    {
        builderContext.AddRequestTransform(async transformContext =>
            await requestTransform.TransformAsync(transformContext));

        builderContext.AddResponseTransform(async transformContext =>
            await responseTransform.TransformAsync(transformContext));
    });

    // Kestrel + ThreadPool tuning by profile. Profiles are chosen via the
    // STYLOBOT_PROFILE env var (or --profile CLI flag) and trade memory for
    // latency / concurrency headroom. See docs/perf-profiles.md.
    //
    // Default: "balanced" (50/50 threads, 10k connections, slowloris-safe).
    //   api       -- low-concurrency, high-RPS JSON endpoints; tighter timeouts
    //   site      -- popular public site (mixed browsers + SignalR); more streams,
    //                generous keep-alive, more upgraded connections for WebSockets
    //   highrisk  -- under attack; aggressive timeouts, low connection cap,
    //                tight slowloris caps, fast-refuse posture
    //   economy   -- constrained-resource deployment (Pi-class / 256MB
    //                container / serverless cold start); smallest thread
    //                pool + lowest connection cap that still serves traffic.
    //                Set automatically by `-e / --economy` along with
    //                STYLOBOT_INMEMORY=1.
    //   balanced  -- default; safe middle ground for unknown workload
    var profile = (Environment.GetEnvironmentVariable("STYLOBOT_PROFILE")
                   ?? args.SkipWhile(a => a != "--profile").Skip(1).FirstOrDefault()
                   ?? "balanced").ToLowerInvariant();

    var (minWorker, minIocp, maxConn, maxUpgraded, maxBody, keepAlive, headerTimeout,
         minDataRate, h2Streams, h2Window) = profile switch
    {
        // API: many short JSON calls, tight CPU budget per request, no websockets.
        "api"      => (100, 100, 20_000,    100, 64 * 1024,           15,  5, 200,  400, 1 * 1024 * 1024),
        // Popular site: browsers + SignalR; lots of long-lived TCP. Generous KA.
        "site"     => (200, 200, 10_000, 10_000, 1 * 1024 * 1024,    120, 15, 100,  400, 2 * 1024 * 1024),
        // Under attack: shed fast, no patience for slow clients.
        "highrisk" => ( 50,  50,  2_000,    100, 32 * 1024,             5,  3,1000,  100, 64 * 1024),
        // Economy: Pi-class / 256MB container / serverless cold start.
        // Half the thread pool of balanced, an order of magnitude fewer
        // connections, tightest buffers. Pairs with STYLOBOT_INMEMORY=1.
        "economy"  => ( 25,  25,    500,     50, 32 * 1024,            15,  5, 200,   50, 64 * 1024),
        // Balanced default: same as what the 2026-05-31 soak validated.
        _          => ( 50,  50, 10_000,  1_000, 256 * 1024,            30, 10, 100,  200, 1 * 1024 * 1024),
    };

    System.Threading.ThreadPool.SetMinThreads(minWorker, minIocp);
    builder.Services.Configure<Microsoft.AspNetCore.Server.Kestrel.Core.KestrelServerOptions>(opts =>
    {
        opts.Limits.MaxConcurrentConnections          = maxConn;
        opts.Limits.MaxConcurrentUpgradedConnections  = maxUpgraded;   // WebSocket / SignalR
        opts.Limits.MaxRequestBodySize                = maxBody;
        opts.Limits.KeepAliveTimeout                  = TimeSpan.FromSeconds(keepAlive);
        opts.Limits.RequestHeadersTimeout             = TimeSpan.FromSeconds(headerTimeout);
        // Slowloris cap stays on across all profiles. Rate is bytes/min for the
        // attack class, so even our most permissive setting (100 B/s) is safe.
        opts.Limits.MinRequestBodyDataRate            = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
            bytesPerSecond: minDataRate, gracePeriod: TimeSpan.FromSeconds(10));
        opts.Limits.MinResponseDataRate               = new Microsoft.AspNetCore.Server.Kestrel.Core.MinDataRate(
            bytesPerSecond: minDataRate, gracePeriod: TimeSpan.FromSeconds(10));
        opts.Limits.Http2.MaxStreamsPerConnection     = h2Streams;
        opts.Limits.Http2.InitialConnectionWindowSize = h2Window;
    });
    Log.Information(
        "Kestrel profile '{Profile}': threads={Worker}/{Iocp} maxConn={MaxConn} h2Streams={H2}",
        profile, minWorker, minIocp, maxConn, h2Streams);

    // Configure Kestrel for TLS if certificate provided (must be before Build)
    // When tunnel is active without TLS, enable h2c so cloudflared --http2-origin can use HTTP/2
    // to localhost — preventing the Http2FingerprintContributor from penalising proxied browser requests.
    if (useTls)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(portNumber, listenOptions =>
            {
                if (certPath!.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase))
                {
                    listenOptions.UseHttps(certPath, certPassword);
                }
                else
                {
                    // PEM cert + key
                    if (keyPath == null)
                    {
                        Log.Fatal("--key is required when using a .pem certificate");
                        throw new InvalidOperationException("--key is required when using a .pem certificate");
                    }
                    listenOptions.UseHttps(httpsOptions =>
                    {
                        httpsOptions.ServerCertificateSelector = (_, _) =>
                            System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPemFile(certPath, keyPath);
                    });
                }
            });
        });
    }
    else if (tunnelEnabled)
    {
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            kestrel.ListenAnyIP(portNumber, listenOptions =>
            {
                listenOptions.Protocols = Microsoft.AspNetCore.Server.Kestrel.Core.HttpProtocols.Http1AndHttp2;
            });
        });
    }

    var app = builder.Build();

    // Load signatures from JSON-L files on startup
    await SignatureLoaderService.LoadSignaturesFromJsonL(app.Services, Log.Logger);

    // --config-dir: drop YAML policy/application files in a directory.
    // Operators can version-control policies and mount them via Docker/K8s.
    var configDirPath = GetArg(cmdArgs, "--config-dir");
    if (!string.IsNullOrWhiteSpace(configDirPath) && Directory.Exists(configDirPath))
    {
        var yamlStore = app.Services.GetService<Mostlylucid.BotDetection.Policies.Templates.YamlTemplateStore>();
        if (yamlStore is not null)
        {
            var apps = yamlStore.LoadApplicationsFromDirectory(configDirPath);
            Log.Information("Loaded {Count} policy application(s) from {Dir}", apps.Count, configDirPath);
        }
    }

    // Use Forwarded Headers middleware FIRST to extract real client IP
    app.UseForwardedHeaders();

    var allowPublicMetrics = builder.Configuration.GetValue("Telemetry:AllowPublicMetricsEndpoint", false) ||
                             bool.TryParse(Environment.GetEnvironmentVariable("STYLOBOT_ALLOW_PUBLIC_METRICS"),
                                 out var allowPublicMetricsEnv) &&
                             allowPublicMetricsEnv;

    app.Use(async (context, next) =>
    {
        if (string.Equals(context.Request.Path.Value, "/metrics", StringComparison.OrdinalIgnoreCase) &&
            !allowPublicMetrics &&
            !IsLoopbackRequest(context))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next();
    });

    // Anti-spoofing: drop X-Bot-Detection-* verdict headers a visitor attached
    // (this gateway computes its own and re-emits via UseStyloBotForwardedHeaders),
    // and — when ForwardedHeaders:StripInboundClientSignalHeaders is enabled —
    // client-signal headers (X-JA3-*, X-Client-TLS-*, …) that only a trusted
    // upstream proxy may inject. Must run before detection reads headers.
    app.UseStyloBotInboundClientHeaderStrip();

    // Tap detection results for the live table (placed BEFORE bot detection
    // so it wraps around it and always sees the evidence in Items after _next completes)
    if (!verbose)
        app.UseMiddleware<DetectionTapMiddleware>();

    // Use Bot Detection middleware
    app.UseBotDetection();

    // Health check endpoint (AOT-compatible) - mapped BEFORE YARP to avoid being proxied
    app.MapGet("/health", () => Results.Text("{\"status\":\"healthy\"}", "application/json"));

    // REST API surface (opt-in via --enable-api). Maps /api/v1/* before YARP so the
    // proxy never sees those requests. Auth enforced per-route by ApiKeyAuthenticationHandler.
    if (enableApi)
    {
        app.UseAuthentication();
        app.UseAuthorization();
        // Persists detections to the event store + broadcasts SignalR invalidation beacons.
        // Without this the read endpoints would always return empty - the gateway has no
        // place to write detection data.
        app.UseBotDetectionPersistence();
        app.MapStyloBotApi();
        // Used by the soak harness to observe persistence backpressure. Keep this
        // operational surface opt-in and protected by the same API-key policy.
        app.MapPersistenceStatsEndpoints();
    }

    // Attach X-Bot-Detection-* headers to the proxied request so any downstream
    // dashboard host's StyloBotForwardedHeadersHydratorMiddleware can populate
    // HttpContext.Items[SignalKeys.SignatureMultifactor] etc. without doing its
    // own detection. Required for a remote-mode website's "You: Bot/Human X%"
    // pill and signature-detail radar to resolve -- without this, the website
    // regenerates a fresh local signature that doesn't match what the gateway
    // recorded and the pill sticks on "Detection pending..." forever. Must run
    // AFTER UseBotDetection and BEFORE MapReverseProxy. Mirrors the wiring in
    // Stylobot.Gateway/Program.cs.
    app.UseStyloBotForwardedHeaders();

    app.MapPrometheusScrapingEndpoint("/metrics");

    // Demo-only local surfaces must be mapped BEFORE YARP to avoid being proxied.
    if (isDemoLikeMode)
    {
        // Serve embedded test page
        app.MapGet("/test-client-side.html", async (HttpContext context) =>
        {
            var assembly = typeof(Program).Assembly;
            var resourceName = "wwwroot.test-client-side.html";

            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null) return Results.NotFound($"Embedded resource '{resourceName}' not found");

            using var reader = new StreamReader(stream);
            var content = await reader.ReadToEndAsync();
            context.Response.Headers.CacheControl = "no-store";
            return Results.Content(content, "text/html");
        });

        app.MapGet("/api/bot-detection/test-status", (HttpContext context, IMemoryCache cache) =>
        {
            context.Response.Headers.CacheControl = "no-store";

            var callbackToken = Guid.NewGuid().ToString("N");
            cache.Set(
                GetDemoClientSideSessionCacheKey(callbackToken),
                new DemoClientSideSession(
                    context.IsBot(),
                    context.GetBotConfidence(),
                    context.GetBotName(),
                    NormalizeRemoteIp(context),
                    DateTimeOffset.UtcNow),
                TimeSpan.FromMinutes(5));

            var callbackUrl =
                $"{context.Request.Scheme}://{context.Request.Host}/api/bot-detection/client-result";
            var responseJson = BuildDemoTestStatusJson(
                context.IsBot(),
                context.GetBotConfidence(),
                context.GetBotName(),
                callbackUrl,
                callbackToken);

            return Results.Text(responseJson, "application/json");
        });

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
                            "url": "https://stylo.bot"
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

        app.MapPost("/api/bot-detection/client-result", async (HttpContext context, IMemoryCache cache) =>
        {
            const long maxRequestBytes = 16 * 1024;

            context.Response.Headers.CacheControl = "no-store";

            if (!string.Equals(context.Request.ContentType, "application/json", StringComparison.OrdinalIgnoreCase) &&
                !(context.Request.ContentType?.StartsWith("application/json;", StringComparison.OrdinalIgnoreCase) ??
                  false))
                return Results.Text("{\"status\":\"error\",\"message\":\"Content-Type must be application/json\"}",
                    "application/json", statusCode: StatusCodes.Status415UnsupportedMediaType);

            if (context.Request.ContentLength is > maxRequestBytes)
                return Results.Text("{\"status\":\"error\",\"message\":\"Request body too large\"}", "application/json",
                    statusCode: StatusCodes.Status413PayloadTooLarge);

            if (!context.Request.Headers.TryGetValue("X-Stylobot-Test-Token", out var testTokenValues) ||
                string.IsNullOrWhiteSpace(testTokenValues))
                return Results.Text("{\"status\":\"error\",\"message\":\"Missing demo callback token\"}",
                    "application/json", statusCode: StatusCodes.Status400BadRequest);

            var callbackToken = testTokenValues.ToString();
            var cacheKey = GetDemoClientSideSessionCacheKey(callbackToken);
            if (!cache.TryGetValue<DemoClientSideSession>(cacheKey, out var session) || session is null)
                return Results.Text("{\"status\":\"error\",\"message\":\"Invalid or expired demo callback token\"}",
                    "application/json", statusCode: StatusCodes.Status400BadRequest);

            cache.Remove(cacheKey);

            if (!string.Equals(session.RemoteIp, NormalizeRemoteIp(context), StringComparison.Ordinal))
            {
                Log.Warning("[CLIENT-SIDE-CALLBACK] Demo callback token IP mismatch");
                return Results.Text("{\"status\":\"error\",\"message\":\"Demo callback token did not match this client\"}",
                    "application/json", statusCode: StatusCodes.Status400BadRequest);
            }

            try
            {
                using var jsonDoc = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                var root = jsonDoc.RootElement;

                if (!root.TryGetProperty("clientChecks", out var clientChecks) ||
                    clientChecks.ValueKind != JsonValueKind.Object)
                    return Results.Text("{\"status\":\"error\",\"message\":\"Missing clientChecks payload\"}",
                        "application/json", statusCode: StatusCodes.Status400BadRequest);

                var hasCanvas = clientChecks.TryGetProperty("hasCanvas", out var canvas) && canvas.GetBoolean();
                var hasWebGL = clientChecks.TryGetProperty("hasWebGL", out var webgl) && webgl.GetBoolean();
                var hasAudioContext = clientChecks.TryGetProperty("hasAudioContext", out var audio) && audio.GetBoolean();
                var pluginCount = clientChecks.TryGetProperty("pluginCount", out var plugins) ? plugins.GetInt32() : 0;
                var hardwareConcurrency = clientChecks.TryGetProperty("hardwareConcurrency", out var hardware)
                    ? hardware.GetInt32()
                    : 0;

                var clientBotScore = CalculateClientBotScore(
                    hasCanvas,
                    hasWebGL,
                    hasAudioContext,
                    pluginCount,
                    hardwareConcurrency);

                var mismatch = (session.ServerIsBot && clientBotScore < 0.3) ||
                               (!session.ServerIsBot && clientBotScore > 0.7);

                if (mismatch)
                    Log.Warning(
                        "[CLIENT-SIDE-MISMATCH] Demo server verdict ({ServerIsBot}, {ServerProb:F2}) conflicts with client score ({ClientScore:F2})",
                        session.ServerIsBot,
                        session.ServerProbability,
                        clientBotScore);
                else
                    Log.Information(
                        "[CLIENT-SIDE-CALLBACK] Demo client-side validation accepted: server={ServerIsBot} ({ServerProb:F2}), client={ClientScore:F2}",
                        session.ServerIsBot,
                        session.ServerProbability,
                        clientBotScore);

                var responseJson = BuildDemoClientCallbackJson(
                    session,
                    clientBotScore,
                    hasCanvas,
                    hasWebGL,
                    hasAudioContext,
                    pluginCount,
                    hardwareConcurrency,
                    mismatch);

                return Results.Text(responseJson, "application/json");
            }
            catch (JsonException ex)
            {
                Log.Warning(ex, "Failed to parse client-side demo callback payload");
                return Results.Text("{\"status\":\"error\",\"message\":\"Invalid JSON payload\"}", "application/json",
                    statusCode: StatusCodes.Status400BadRequest);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Failed to process client-side demo callback");
                return Results.Text("{\"status\":\"error\",\"message\":\"Invalid request\"}", "application/json",
                    statusCode: StatusCodes.Status400BadRequest);
            }
        });
    }

    // Map YARP reverse proxy (catch-all, should be LAST)
    app.MapReverseProxy();

    // Set listen URL (when not using TLS; TLS uses ConfigureKestrel before Build)
    if (!useTls)
        app.Urls.Add($"http://*:{portNumber}");

    var scheme = useTls ? "https" : "http";
    if (verbose)
    {
        Log.Information("  ✓ Ready on {Scheme}://localhost:{Port}", scheme, port);
        Log.Information("  ✓ Upstream: {Upstream}", upstream);
        Log.Information("  ✓ Health:   {Scheme}://localhost:{Port}/health", scheme, port);
        Log.Information("");
    }

    // Shared tunnel URL (populated by cloudflared log parser, read by live table)
    string? tunnelUrl = null;

    // -----------------------------------------------------------------------
    // Start-safe startup sequence:
    //   1. Start a live status panel showing each step.
    //   2. Bring up Kestrel via app.StartAsync() so the local listener is
    //      definitely serving before anything else can fail.
    //   3. Launch Cloudflare tunnel (if requested) AFTER the listener is up so
    //      a tunnel failure cannot prevent the local port from being usable.
    //   4. Hand off to the live detection table and block on WaitForShutdownAsync.
    // -----------------------------------------------------------------------
    var bannerEnabled = !verbose && !System.Console.IsOutputRedirected;
    // When the live banner is suppressed (verbose, redirected stdout, daemon),
    // mirror startup task transitions through the standard log so operators
    // still see resource downloads / Ollama pulls / tunnel events happening.
    var statusBoard = new StartupStatusBoard(
        echoLogger: bannerEnabled ? null : app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Stylobot.Startup"));
    CancellationTokenSource? bannerCts = null;
    Task? bannerTask = null;
    StartupBannerRenderer? banner = null;

    if (bannerEnabled)
    {
        banner = new StartupBannerRenderer(statusBoard, "StyloBot starting up");
        bannerCts = new CancellationTokenSource();
        bannerTask = banner.RunUntilSettledAsync(bannerCts.Token);
    }

    Process? tunnelProcess = null;
    CancellationTokenSource? liveTableCts = null;
    Task? liveTableTask = null;

    try
    {
        statusBoard.Start("listener", $"Starting HTTP listener on port {portNumber}");
        try
        {
            await app.StartAsync();
            statusBoard.Done("listener", $"{scheme}://localhost:{portNumber}");
        }
        catch (Exception ex)
        {
            statusBoard.Fail("listener", ex.Message);
            banner?.StopAndFlush();
            Log.Fatal(ex, "Failed to start HTTP listener");
            throw;
        }

        if (skipDependencyChecks)
        {
            statusBoard.Skip("dependencies", "Dependency checks",
                "--skip-dependencies-check set; using whatever is already on disk");
        }
        else
        {
            // Setup resources (bot list, ONNX model, GeoIP CSV). Each ISetupResource is
            // checked and downloaded if missing or stale. Runs after the listener is up
            // so the local port serves immediately while large CSVs / model files stream
            // in. Every failure is captured as a Failed row and the host keeps going so
            // a broken registry never blocks the local port from serving.
            try
            {
                await StartupSetupBridge.RunAsync(app.Services, statusBoard);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "Setup resource bridge failed; detection will use last-known data on disk");
            }

            // LLM model presence + auto-pull (when --llm ollama is set). Runs after the
            // listener is up so the local port is already serving while a large model
            // download streams in. Detection works without LLM enrichment in the
            // meantime; the LLM coordinator picks up the model when it appears.
            if (llmProvider?.Equals("ollama", StringComparison.OrdinalIgnoreCase) == true)
            {
                var ollamaEndpoint = llmUrl ?? Mostlylucid.BotDetection.Models.LlmDefaults.DefaultEndpoint;
                var ollamaModel    = llmModel ?? Mostlylucid.BotDetection.Models.LlmDefaults.DefaultModel;
                try
                {
                    await OllamaModelManager.EnsureModelAsync(ollamaEndpoint, ollamaModel, statusBoard);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "Ollama model setup failed; LLM enrichment will be unavailable");
                }
            }
        }

        if (tunnelEnabled)
        {
            statusBoard.Start("tunnel",
                tunnelToken != null ? "Launching Cloudflare tunnel (named)" : "Launching Cloudflare tunnel (quick)");
            try
            {
                tunnelProcess = CloudflaredTunnelLauncher.Launch(portNumber, scheme, tunnelToken, url => tunnelUrl = url);
                if (tunnelProcess == null)
                {
                    statusBoard.Fail("tunnel", "cloudflared not found");
                }
                else
                {
                    statusBoard.Done("tunnel", "cloudflared launched");
                    statusBoard.Start("tunnel-url", "Waiting for public URL");
                    var urlDeadline = DateTime.UtcNow.AddSeconds(20);
                    while (tunnelUrl is null && DateTime.UtcNow < urlDeadline && !tunnelProcess.HasExited)
                        await Task.Delay(200);
                    if (tunnelUrl != null) statusBoard.Done("tunnel-url", tunnelUrl);
                    else if (tunnelProcess.HasExited) statusBoard.Fail("tunnel-url", "cloudflared exited before publishing URL");
                    else statusBoard.Fail("tunnel-url", "timed out waiting for URL");
                }
            }
            catch (Exception ex)
            {
                // App stays usable on local port even if tunnel fails - this is the safety win.
                statusBoard.Fail("tunnel", ex.Message);
                Log.Warning(ex, "Cloudflare tunnel failed to launch; local listener remains active");
            }
        }
        else
        {
            statusBoard.Skip("tunnel", "Cloudflare tunnel", "not requested (--tunnel not set)");
        }

        // Stop the banner and let the alt-screen live table take over.
        if (banner != null && bannerCts != null && bannerTask != null)
        {
            await bannerCts.CancelAsync();
            try { await bannerTask; } catch (OperationCanceledException) { }
        }

        if (!verbose)
        {
            var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
            liveTableCts = CancellationTokenSource.CreateLinkedTokenSource(lifetime.ApplicationStopping);
            var liveTable = new LiveDetectionTableService(
                detectionSink, mode, upstream, port, actionPolicy ?? "auto",
                useTls, tunnelEnabled, () => tunnelUrl);
            liveTableTask = liveTable.StartAsync(liveTableCts.Token);
        }
        else
        {
            Log.Information("  Press Ctrl+C to stop.");
        }

        await app.WaitForShutdownAsync();
        Log.Information("Application shutdown requested (Ctrl+C or SIGTERM)");
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
        if (banner != null) banner.StopAndFlush();
        bannerCts?.Dispose();

        if (liveTableCts != null)
        {
            await liveTableCts.CancelAsync();
            if (liveTableTask != null)
                try { await liveTableTask; } catch (OperationCanceledException) { }
            liveTableCts.Dispose();
        }

        if (tunnelProcess is { HasExited: false })
        {
            Log.Information("Stopping Cloudflare tunnel...");
            tunnelProcess.Kill(entireProcessTree: true);
            tunnelProcess.Dispose();
        }

        await signatureLogger.FlushAndStopAsync();
    }
}
catch (Exception ex)
{
    Log.Fatal(ex, "Application startup or configuration failed");
    Console.Error.WriteLine($"  Startup failed: {ex.Message}");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

return 0;

// Hashes a password for StyloBot:Dashboard:Auth:PasswordHash. The hash goes to
// stdout (pipeable); prompts/guidance go to stderr so `... hash-password > out` is clean.
// --password <pw> allows non-interactive use (scripts/CI); otherwise prompt without echo.
static int RunDashboardHashPassword(string[] args)
{
    var password = GetArg(args, "--password");
    if (string.IsNullOrEmpty(password))
    {
        Console.Error.Write("Password: ");
        password = ReadPasswordNoEcho();
        Console.Error.WriteLine();
        Console.Error.Write("Confirm : ");
        var confirm = ReadPasswordNoEcho();
        Console.Error.WriteLine();
        if (password != confirm)
        {
            Console.Error.WriteLine("Passwords did not match.");
            return 1;
        }
    }

    if (string.IsNullOrEmpty(password))
    {
        Console.Error.WriteLine("Password must not be empty.");
        return 1;
    }

    var hash = DashboardPasswordHasher.Hash(password);
    Console.Error.WriteLine("Set StyloBot:Dashboard:Auth (Mode=Login, Username=...) and paste this as PasswordHash:");
    Console.WriteLine(hash);
    return 0;
}

// Reads a line without echoing keystrokes. Falls back to a normal read when input
// is redirected (piped/CI) or no console is attached.
static string ReadPasswordNoEcho()
{
    if (Console.IsInputRedirected)
        return Console.ReadLine() ?? string.Empty;

    var sb = new StringBuilder();
    while (true)
    {
        ConsoleKeyInfo key;
        try
        {
            key = Console.ReadKey(intercept: true);
        }
        catch (InvalidOperationException)
        {
            // No interactive console (e.g. some CI shells): fall back to a plain read.
            return Console.ReadLine() ?? sb.ToString();
        }

        if (key.Key == ConsoleKey.Enter) break;
        if (key.Key == ConsoleKey.Backspace)
        {
            if (sb.Length > 0) sb.Length--;
            continue;
        }
        if (!char.IsControl(key.KeyChar)) sb.Append(key.KeyChar);
    }

    return sb.ToString();
}

static async Task<int> RunDashboardAsync(string[] args)
{
    // stylobot dashboard hash-password -- print a PBKDF2 hash to paste into
    // StyloBot:Dashboard:Auth:PasswordHash (FOSS config-credential dashboard login).
    if (args.Length > 2 && args[2].Equals("hash-password", StringComparison.OrdinalIgnoreCase))
        return RunDashboardHashPassword(args);

    // stylobot dashboard <url> [--api-key <key>]
    var apiKey = GetArg(args, "--api-key") ?? GetArg(args, "--apikey");
    string? url = null;

    for (var i = 2; i < args.Length; i++)
    {
        if (!args[i].StartsWith('-'))
        {
            url = args[i];
            break;
        }
    }

    if (url == null)
    {
        Console.Error.WriteLine("  Usage: stylobot dashboard <url> [--api-key <key>]");
        Console.Error.WriteLine("  Example: stylobot dashboard https://mysite.com --api-key mykey");
        return 1;
    }

    if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed) ||
        (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
    {
        Console.Error.WriteLine($"  Invalid URL: {url}");
        return 1;
    }

    return await Mostlylucid.BotDetection.Console.Services.RemoteDashboardTui.RunAsync(url, apiKey);
}

static async Task<int> ClearLearnedPatternsAsync(string[] args)
{
    var clearSessions = args.Contains("--sessions", StringComparer.OrdinalIgnoreCase);

    // Resolve DB path using same priority as the server
    var dbPath = Environment.GetEnvironmentVariable("STYLOBOT_DatabasePath")
        ?? Path.Combine(Mostlylucid.BotDetection.Models.BotDetectionOptions.ResolveDataDirectory(), "botdetection.db");

    if (!File.Exists(dbPath))
    {
        Console.WriteLine($"  No database found at: {dbPath}");
        Console.WriteLine("  Nothing to clear.");
        return 0;
    }

    try
    {
        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        await conn.OpenAsync();

        await using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand("DELETE FROM learned_patterns", conn))
        {
            var rows = await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"  Cleared {rows} learned pattern(s).");
        }

        if (clearSessions)
        {
            await using var cmd = new Microsoft.Data.Sqlite.SqliteCommand(
                "DELETE FROM sessions; DELETE FROM signatures; DELETE FROM buckets", conn);
            var rows = await cmd.ExecuteNonQueryAsync();
            Console.WriteLine($"  Cleared {rows} session/signature/bucket row(s).");
        }

        await using (var cmd = new Microsoft.Data.Sqlite.SqliteCommand("VACUUM", conn))
        {
            await cmd.ExecuteNonQueryAsync();
        }

        Console.WriteLine("  Done. Restart stylobot to pick up the fresh state.");
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"  Failed to clear database: {ex.Message}");
        return 1;
    }
}

static void ShowManPage()
{
    var man = """
    [bold blue]STYLOBOT(1)[/]                    [dim]Self-hosted bot defense[/]                    [bold blue]STYLOBOT(1)[/]

    [bold]NAME[/]
        stylobot - reverse proxy with 32-detector bot defense. Free forever.

    [bold]SYNOPSIS[/]
        [bold]stylobot[/] <port> <upstream> [[options]]
        [bold]stylobot[/] start <port> <upstream> [[options]]
        [bold]stylobot[/] stop | status | logs | man

    [bold]DESCRIPTION[/]
        StyloBot proxies HTTP traffic to an upstream server while running real-time
        bot detection. 67 detectors analyze every request in <1ms, including
        Threat Intelligence (Spamhaus DROP / Tor exit / CISA KEV / cloud ranges)
        and behavioural pattern matching. Results display in a live terminal
        table with color-coded verdicts.

        Detection works fully without any LLM. LLM enrichment (optional) adds bot
        naming and intent classification asynchronously.

        Out of the box (6.8+): malicious bots are blocked, search/AI bots are
        rate-limited, humans are untouched, and bots get throttled harder when
        the origin starts slowing down. See the policy-defaults doc on GitHub
        for the full per-BotType mapping.

    [bold]COMMANDS[/]
        [bold]start[/]     Start as a background daemon (writes PID file)
        [bold]stop[/]      Stop the running daemon (SIGTERM)
        [bold]status[/]    Check if daemon is running + hit /health
        [bold]logs[/]      Show recent log output
        [bold]man[/]       This manual page

        [bold]llmtunnel[/] [[token]]
                  Start a local LLM agent and Cloudflare tunnel so a remote StyloBot
                  site can use your local GPU for bot classification.
                  'token' is an optional named Cloudflare tunnel token.
                  Without it, an anonymous quick tunnel is used.

        [bold]LLM Tunnel Options[/]
          [bold]--ollama[/] <url>         Local Ollama URL (default: http://127.0.0.1:11434)
          [bold]--models[/] <csv>         Comma-separated model allowlist
          [bold]--max-concurrency[/] <n>  Max concurrent inference requests (default: 2)
          [bold]--max-context[/] <tokens> Max context tokens (default: 8192)
          [bold]--agent-port[/] <port>    Loopback agent port (default: random)

        [bold]LLM Tunnel Examples[/]
          stylobot llmtunnel
          stylobot llmtunnel eyJhIjoiNjQ2...              Named permanent tunnel
          stylobot llmtunnel --models llama3.2:3b,qwen2.5:14b
          stylobot llmtunnel --max-concurrency 4 --max-context 16384

        [bold]LLM Tunnel Security[/]
          - Anonymous tunnel URLs are NOT credentials. The connection key IS.
          - Connection keys are sensitive. Treat them like API keys.
          - Named Cloudflare tunnels provide stable hostnames for production use.
          - Quick tunnels are ephemeral: re-import the key after each restart.
          - Imported nodes can be revoked via DELETE /api/v1/llm-nodes/[[nodeId]].
          - Every inference request is signed and replay-protected (30s TTL).
          - Raw Ollama endpoints are never exposed through the tunnel.
          - Optional AES-GCM payload encryption can be enabled for stronger privacy.

    [bold]OPTIONS[/]
        [bold]--port[/] <port>                   Listen port (alternative to positional)
        [bold]--upstream[/] <url>                Upstream URL (alternative to positional)
        [bold]--mode[/] <demo|production>       Detection mode (default: demo -- observe only;
                                               override to production to enable policy actions)
        [bold]--policy[/] <name>                Action policy override. Default action grammar (6.8+)
                                               routes per BotType: MaliciousBot -> block-hard,
                                               AiBot -> extract-markdown, SearchEngine -> rate-limit-search,
                                               etc. Override the fallback (used when no per-type entry
                                               matches): logonly | block | throttle-stealth | challenge.
        [bold]--threshold[/] <0.0-1.0>          Bot probability threshold (default: 0.7)
        [bold]--cert[/] <path>                  TLS certificate (.pfx or .pem)
        [bold]--key[/] <path>                   TLS private key (with .pem cert)
        [bold]--cert-password[/] <pass>         PFX certificate password
        [bold]--tunnel[/] [[token]]               Cloudflare Tunnel for public ingress (Tunnel A).
                                               No token: quick tunnel (*.trycloudflare.com URL,
                                               printed on startup). With token: named tunnel from
                                               your CF dashboard. Requires cloudflared on PATH.
        [bold]--origin-tunnel[/] <hostname>      (6.8.2+) Reach a private origin through a Cloudflare
                                               Tunnel (Tunnel B). Stylobot picks a free loopback port,
                                               launches `cloudflared access tcp --hostname <h> --url
                                               localhost:<port>`, and uses that as its upstream. Lets
                                               your legacy box have ZERO public exposure. Pairs with
                                               --tunnel for the full brownfield retrofit.
        [bold]--llm[/] <provider>               LLM: openai, anthropic, gemini, groq, ollama...
        [bold]--llm-key[/] <key>                API key (or env STYLOBOT_LLM_KEY)
        [bold]--llm-url[/] <url>                Custom provider base URL
        [bold]--model[/] <name>                 Override default model for provider
        [bold]--config[/] <path>                Custom appsettings.json
        [bold]--config-dir[/] <path>            Load YAML policy/application files from directory
                                               (version-control policies, mount via Docker/K8s)
        [bold]--log-level[/] <level>            Minimum log level (default: Warning)
        [bold]--verbose[/]                      Full log output (disables live table)
        [bold]--enable-api[/]                   Expose /api/v1/* REST endpoints + SignalR hub
                                               (requires BotDetection:ApiKeys configured)
        [bold]--origin-tunnel[/] <hostname>      Reach private origin through Cloudflare Tunnel B
        [bold]--profile[/] <name>               Kestrel profile: balanced|api|site|highrisk|economy
                                               (also settable via STYLOBOT_PROFILE env)
        [bold]--skip-dependencies-check[/]       Skip first-run downloads (bot list, GeoIP, Ollama)

    [bold]DASHBOARD AUTH[/]
        [bold]stylobot dashboard hash-password[/]
                Generate a PBKDF2 hash for StyloBot:Dashboard:Auth:PasswordHash.
                Output goes to stdout (pipeable); prompts go to stderr.
                --password <pw> skip interactive prompt (scripts/CI).

        [bold]stylobot dashboard[/] <url> [--api-key <key>]
                Remote dashboard TUI for another stylobot instance.

    [bold]POLICY GRAMMAR (6.8+)[/]
        Action policies are now intent-keyed (Pass / Block / RateLimit / Throttle / Challenge)
        and route per-BotType automatically. Set per-policy overrides in appsettings.json:

          BotDetection:BotTypeActionPolicies.MaliciousBot   = "block-hard"        (default)
          BotDetection:BotTypeActionPolicies.AiBot          = "extract-markdown"  (default)
          BotDetection:BotTypeActionPolicies.SearchEngine   = "rate-limit-search" (default)
          BotDetection:BotTypeActionPolicies.Tool           = "throttle-tools"    (default)
          ...

        Set BotDetection:ObserveOnly = true to shadow every policy through logonly
        (the dashboard still records which policy *would* have fired). Use during
        pre-launch calibration. Replaces the 6.7 implicit observe-only posture.

        Adaptive scaling: when upstream P95 latency >= 1000ms or 5xx rate >= 3%,
        bot rate limits scale down (humans don't traverse rate limits, so they
        get priority). Configure via BotDetection:RateLimit:AdaptiveScaling.

    [bold]LLM PROVIDERS[/]
        Any provider, any tier. Bring your own API key.

        [dim]Provider     Model               Cost[/]
        openai       gpt-4o-mini         ~$0.15/1M tokens
        anthropic    claude-haiku-4-5     ~$0.25/1M tokens
        gemini       gemini-2.0-flash     Free tier
        groq         llama-3.3-70b        Free tier
        mistral      mistral-small        ~$0.10/1M tokens
        deepseek     deepseek-chat        ~$0.07/1M tokens
        ollama       gemma4               Free (local)
        llamasharp   qwen2.5:0.5b         Free (in-process CPU)

    [bold]ENVIRONMENT[/]
        PORT                 Listen port
        UPSTREAM             Upstream URL
        MODE                 Detection mode
        STYLOBOT_POLICY      Default action policy
        STYLOBOT_LLM_KEY     LLM API key
        KNOWN_NETWORKS       Trusted proxy CIDRs (comma-separated)
        KNOWN_PROXIES        Trusted proxy IPs (comma-separated)
        STYLOBOT_ALLOW_PUBLIC_METRICS
                             Allow remote scraping of /metrics
        /run/secrets/<key>   Docker/K8s secrets mounted as config keys
                             (e.g. /run/secrets/STYLOBOT_LLM_KEY → STYLOBOT_LLM_KEY)

    [bold]CONFIGURATION[/]
        Config priority (highest first):
          1. CLI flags
          2. Environment variables (STYLOBOT_* prefix)
          3. --config <file>
          4. appsettings.{mode}.json
          5. appsettings.json

        Full reference: https://github.com/scottgal/stylobot/blob/main/src/Mostlylucid.BotDetection/docs/configuration-reference.md
        Policy defaults: https://github.com/scottgal/stylobot/blob/main/src/Mostlylucid.BotDetection/docs/policy-defaults.md
        Brownfield retrofit: https://github.com/scottgal/stylobot/blob/main/docs/brownfield-retrofit.md

    [bold]EXAMPLES[/]
        stylobot 5080 http://localhost:3000
        stylobot 5080 http://localhost:3000 --mode demo
        stylobot 8000 http://api.mysite.com --mode production
        stylobot 5080 http://localhost:3000 --tunnel
        stylobot 5080 http://localhost:3000 --llm groq --llm-key gsk-...
        stylobot start 443 https://backend:8080 --cert cert.pfx
        stylobot 5080 http://localhost:3000 --enable-api    # Expose REST API for stylobot-ui
        stylobot dashboard hash-password                    # Generate auth hash for config
        stylobot dashboard https://gateway.example.com --api-key mykey   # Remote dashboard TUI
        # Brownfield retrofit: private origin via Cloudflare Tunnel B,
        # public ingress via Tunnel A. Old box has zero public exposure.
        stylobot 5080 --origin-tunnel oldsite.tunnel.example.org --tunnel <ingress-token>

    [bold]ENDPOINTS[/]
        /health          Health check (minimal JSON)
        /metrics         Prometheus metrics (loopback-only by default)
        /test-client-side.html, /api/bot-detection/test-status,
        /api/bot-detection/client-result
                         Demo/learning mode only
        /**              All other paths proxied with detection

    [bold]FILES[/]
        ~/.config/stylobot/stylobot.pid     Daemon PID file
        ~/.config/stylobot/stylobot.port    Daemon port file
        ./appsettings.json                  Default configuration
        ./logs/                             Log output directory

    [bold]SEE ALSO[/]
        https://stylo.bot
        https://github.com/scottgal/stylobot

    [dim]StyloBot Community Edition                Free forever                  v8.7.0[/]
    """;

    // Render [bold]...[/] and [dim]...[/] tags with ANSI codes (no Spectre dependency)
    var rendered = System.Text.RegularExpressions.Regex.Replace(man, @"\[bold\](.*?)\[/\]", "\x1b[1m$1\x1b[0m");
    rendered = System.Text.RegularExpressions.Regex.Replace(rendered, @"\[dim\](.*?)\[/\]", "\x1b[2m$1\x1b[0m");
    Console.WriteLine(rendered);
}

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

// (Old CheckOllamaAsync helper removed; superseded by OllamaModelManager.EnsureModelAsync
//  which runs inside the startup banner phase and supports auto-pull with streamed progress.)

// Helper to get command-line argument value
static string? GetArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            return args[i + 1];

    return null;
}

static bool TryParseNetwork(string value, out System.Net.IPNetwork network)
{
    network = default;
    var parts = value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (parts.Length != 2 ||
        !IPAddress.TryParse(parts[0], out var prefix) ||
        !int.TryParse(parts[1], out var prefixLength))
        return false;

    // Validate prefix length range (IPv4: 0-32, IPv6: 0-128)
    var maxPrefix = prefix.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
    if (prefixLength < 0 || prefixLength > maxPrefix)
        return false;

    network = new System.Net.IPNetwork(prefix, prefixLength);
    return true;
}

static bool IsLoopbackRequest(HttpContext context)
{
    var remoteIp = context.Connection.RemoteIpAddress;
    if (remoteIp == null)
        return false;

    if (remoteIp.IsIPv4MappedToIPv6)
        remoteIp = remoteIp.MapToIPv4();

    return IPAddress.IsLoopback(remoteIp);
}

static string NormalizeRemoteIp(HttpContext context)
{
    var remoteIp = context.Connection.RemoteIpAddress;
    if (remoteIp == null)
        return "unknown";

    if (remoteIp.IsIPv4MappedToIPv6)
        remoteIp = remoteIp.MapToIPv4();

    return remoteIp.ToString();
}

static string GetDemoClientSideSessionCacheKey(string callbackToken) =>
    $"Stylobot:DemoClientSide:{callbackToken}";

static string BuildDemoTestStatusJson(
    bool isBot,
    double probability,
    string? botName,
    string callbackUrl,
    string callbackToken)
{
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    writer.WriteStartObject();
    writer.WriteString("status", "ready");
    writer.WriteStartObject("serverDetection");
    writer.WriteBoolean("isBot", isBot);
    writer.WriteNumber("probability", Math.Round(probability, 4));
    if (!string.IsNullOrWhiteSpace(botName))
        writer.WriteString("botName", botName);
    writer.WriteEndObject();
    writer.WriteString("callbackUrl", callbackUrl);
    writer.WriteString("callbackToken", callbackToken);
    writer.WriteEndObject();
    writer.Flush();

    return Encoding.UTF8.GetString(stream.ToArray());
}

static string BuildDemoClientCallbackJson(
    DemoClientSideSession session,
    double clientBotScore,
    bool hasCanvas,
    bool hasWebGL,
    bool hasAudioContext,
    int pluginCount,
    int hardwareConcurrency,
    bool mismatch)
{
    using var stream = new MemoryStream();
    using var writer = new Utf8JsonWriter(stream);

    writer.WriteStartObject();
    writer.WriteString("status", "accepted");
    writer.WriteString("message", "Demo client-side result processed");

    writer.WriteStartObject("serverDetection");
    writer.WriteBoolean("isBot", session.ServerIsBot);
    writer.WriteNumber("probability", Math.Round(session.ServerProbability, 4));
    if (!string.IsNullOrWhiteSpace(session.BotName))
        writer.WriteString("botName", session.BotName);
    writer.WriteEndObject();

    writer.WriteStartObject("clientEvaluation");
    writer.WriteNumber("botScore", Math.Round(clientBotScore, 4));
    writer.WriteBoolean("mismatch", mismatch);
    writer.WriteBoolean("hasCanvas", hasCanvas);
    writer.WriteBoolean("hasWebGL", hasWebGL);
    writer.WriteBoolean("hasAudioContext", hasAudioContext);
    writer.WriteNumber("pluginCount", pluginCount);
    writer.WriteNumber("hardwareConcurrency", hardwareConcurrency);
    writer.WriteEndObject();

    writer.WriteEndObject();
    writer.Flush();

    return Encoding.UTF8.GetString(stream.ToArray());
}

file sealed record DemoClientSideSession(
    bool ServerIsBot,
    double ServerProbability,
    string? BotName,
    string RemoteIp,
    DateTimeOffset IssuedAt);
