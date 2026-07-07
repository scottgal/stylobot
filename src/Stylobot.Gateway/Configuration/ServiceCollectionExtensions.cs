using System.Security.Cryptography.X509Certificates;
using LettuceEncrypt;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Serilog;
using Stylobot.Gateway.Data;
using Stylobot.Gateway.Health;
using Stylobot.Gateway.Services;
using Stylobot.Gateway.Transforms;
using Yarp.ReverseProxy.Configuration;

namespace Stylobot.Gateway.Configuration;

/// <summary>
/// Extension methods for configuring gateway services.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Add gateway configuration from environment and files.
    /// </summary>
    public static IServiceCollection AddGatewayConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind from environment variables
        services.Configure<GatewayOptions>(opts =>
        {
            opts.HttpPort = GetEnvInt("GATEWAY_HTTP_PORT", 8080);
            opts.DefaultUpstream = Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM");
            opts.AdminBasePath = Environment.GetEnvironmentVariable("ADMIN_BASE_PATH") ?? "/admin";
            opts.AdminSecret = Environment.GetEnvironmentVariable("ADMIN_SECRET");
            opts.AllowInsecureAdminAccess = GetEnvBool("ADMIN_ALLOW_INSECURE", false);
            opts.LogLevel = Environment.GetEnvironmentVariable("LOG_LEVEL") ?? "Information";

            // Demo mode can be enabled via environment variable
            var demoModeEnv = Environment.GetEnvironmentVariable("GATEWAY_DEMO_MODE");
            if (bool.TryParse(demoModeEnv, out var demoEnabled))
            {
                opts.DemoMode.Enabled = demoEnabled;
            }
        });

        services.Configure<DatabaseOptions>(opts =>
        {
            var providerStr = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "none";
            opts.Provider = Enum.TryParse<DatabaseProvider>(providerStr, true, out var provider)
                ? provider
                : DatabaseProvider.None;
            opts.ConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");
            opts.MigrateOnStartup = GetEnvBool("DB_MIGRATE_ON_STARTUP", true);
        });

        services.Configure<GatewayOptions>(opts => { opts.Tls = ReadTlsOptionsFromEnv(); });

        // Also bind from configuration section (file-based)
        services.Configure<GatewayOptions>(configuration.GetSection(GatewayOptions.SectionName));
        services.Configure<DatabaseOptions>(configuration.GetSection(DatabaseOptions.SectionName));

        return services;
    }

    /// <summary>
    /// Add database context if configured.
    /// </summary>
    public static IServiceCollection AddGatewayDatabase(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var dbOptions = new DatabaseOptions();

        // Get from env first
        var providerStr = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "none";
        dbOptions.Provider = Enum.TryParse<DatabaseProvider>(providerStr, true, out var provider)
            ? provider
            : DatabaseProvider.None;
        dbOptions.ConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");

        // Fall back to config
        if (dbOptions.Provider == DatabaseProvider.None)
        {
            configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        }

        if (!dbOptions.IsEnabled)
        {
            return services;
        }

        // FOSS Gateway ships no DB providers. Commercial variant binaries
        // (stylobot-commercial repo) bake in their own provider registration
        // at build time via xcaddy-style composition - no runtime plugin
        // loading because NativeAOT can't JIT loaded assemblies.
        services.AddDbContext<GatewayDbContext>(_ => { });

        return services;
    }

    /// <summary>
    /// Add YARP reverse proxy services with TLS fingerprinting transforms.
    /// Configuration precedence (highest to lowest):
    /// 1. DEFAULT_UPSTREAM environment variable (zero-config mode)
    /// 2. YARP config file (yarp.json)
    /// 3. Empty config (no routes) - logs warning
    ///
    /// Automatically adds TLS/TCP/HTTP2 fingerprinting headers for bot detection.
    /// </summary>
    public static IServiceCollection AddYarpServices(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var proxyBuilder = services.AddReverseProxy()
            .ConfigureHttpClient((context, handler) =>
            {
                // Refresh upstream connections every 5 minutes so DNS changes
                // (rolling deploy on the backend, blue/green cutover, ELB DNS
                // rotation) propagate without bouncing the gateway. Default is
                // Timeout.InfiniteTimeSpan -- a connection picked up the first
                // time a hostname resolved stays bound to that IP forever.
                handler.PooledConnectionLifetime = TimeSpan.FromMinutes(5);

                // Multiplexing toggle: HTTP/2 deliberately maintains a single
                // connection per origin to maximise stream re-use. Under high
                // RPS that single connection becomes the throughput cap.
                // Allow multiple H/2 connections per origin so streams spread
                // across them; the YARP-default SocketsHttpHandler then
                // respects MaxConnectionsPerServer (default int.MaxValue
                // already).
                handler.EnableMultipleHttp2Connections = true;
            })
            .AddTransforms(builderContext =>
            {
                // Add TLS/TCP/HTTP2 fingerprinting headers to all proxied requests
                // These headers are consumed by bot detection contributors:
                // - TlsFingerprintContributor
                // - TcpIpFingerprintContributor
                // - Http2FingerprintContributor
                builderContext.AddTlsFingerprintingHeaders();

                // Stamp upstream RTT timing into HttpContext.Items so downstream
                // middleware (AspNet pack absorption) can split observed wall-clock
                // duration into upstream RTT vs StyloBot processing time.
                builderContext.AddUpstreamTimingTransforms();

                // Add bot detection headers transform based on demo mode
                builderContext.AddDemoModeTransform(configuration);
            });

        // Check for DEFAULT_UPSTREAM first (highest priority - zero-config mode)
        var defaultUpstream = Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM");
        if (!string.IsNullOrWhiteSpace(defaultUpstream))
        {
            Console.WriteLine($"[Stylobot.Gateway] Using DEFAULT_UPSTREAM: {defaultUpstream}");
            // Create in-memory config provider for catch-all routing
            services.AddSingleton<IProxyConfigProvider>(sp =>
                new DefaultUpstreamConfigProvider(defaultUpstream));
            return services;
        }

        // Try to load from YARP config file
        var yarpConfigPath = GatewayPaths.YarpConfig;
        if (File.Exists(yarpConfigPath))
        {
            Console.WriteLine($"[Stylobot.Gateway] Loading routes from config file: {yarpConfigPath}");
            var yarpConfig = new ConfigurationBuilder()
                .AddJsonFile(yarpConfigPath, optional: true, reloadOnChange: true)
                .Build();

            proxyBuilder.LoadFromConfig(yarpConfig.GetSection("ReverseProxy"));
        }
        else
        {
            // No config - log warning and use empty provider
            Console.WriteLine("┌─────────────────────────────────────────────────────────────────┐");
            Console.WriteLine("│ WARNING: No proxy routes configured!                            │");
            Console.WriteLine("│                                                                 │");
            Console.WriteLine("│ To configure routes, either:                                    │");
            Console.WriteLine("│   1. Set DEFAULT_UPSTREAM env var for catch-all routing:       │");
            Console.WriteLine("│      -e DEFAULT_UPSTREAM=http://your-backend:3000              │");
            Console.WriteLine("│                                                                 │");
            Console.WriteLine("│   2. Mount a YARP config file:                                  │");
            Console.WriteLine("│      -v ./config:/app/config:ro                                 │");
            Console.WriteLine("│                                                                 │");
            Console.WriteLine("│ The gateway will respond with 404 for all requests.            │");
            Console.WriteLine("└─────────────────────────────────────────────────────────────────┘");
            services.AddSingleton<IProxyConfigProvider, EmptyConfigProvider>();
        }

        return services;
    }

    /// <summary>
    /// Add gateway core services.
    /// </summary>
    public static IServiceCollection AddGatewayServices(this IServiceCollection services)
    {
        services.AddSingleton<GatewayMetrics>();
        services.AddSingleton<ConfigurationService>();
        return services;
    }

    /// <summary>
    /// Add profile mode services: capture channel, calibration store, and background analysis worker.
    /// Services are always registered; the worker self-disables when Enabled=false.
    /// </summary>
    public static IServiceCollection AddProfileMode(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Bind env vars first, then config file (config file wins if both set)
        services.Configure<ProfileModeOptions>(opts =>
        {
            opts.Enabled = GetEnvBool("GATEWAY_PROFILE_MODE", opts.Enabled);
            opts.ChannelCapacity = GetEnvInt("GATEWAY_PROFILE_CHANNEL_CAPACITY", opts.ChannelCapacity);
            opts.Concurrency = GetEnvInt("GATEWAY_PROFILE_CONCURRENCY", opts.Concurrency);
        });
        services.Configure<ProfileModeOptions>(configuration.GetSection(ProfileModeOptions.SectionName));

        // Always register - active only when Enabled=true
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ProfileModeOptions>>().Value;
            return new ProfileAnalysisChannel(opts);
        });

        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ProfileModeOptions>>().Value;
            var dbPath = opts.DatabasePath
                ?? Path.Combine(GatewayPaths.Data, "profile_calibration.db");
            return new ProfileCalibrationStore(dbPath);
        });

        // Wave 2 migrated: subscribes to ScheduleCoordinator.Tick10s at
        // construction. GatewayHostedSingletonsBootstrap eagerly resolves it
        // at boot so the subscription is live before the first tick.
        services.AddSingleton<ProfileAnalysisWorker>();
        services.AddHostedService<GatewayHostedSingletonsBootstrap>();

        return services;
    }

    /// <summary>
    /// Bind upstream health monitor options from configuration.
    /// Monitor services are registered by downstream tasks.
    /// </summary>
    public static IServiceCollection AddUpstreamHealthMonitor(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        services.Configure<UpstreamHealthMonitorOptions>(
            configuration.GetSection(UpstreamHealthMonitorOptions.SectionName));

        // Apply default candidate paths if none were configured.
        // This ensures that unconfigured instances get the full 9-path default,
        // while operator overrides replace (not append) the default.
        services.PostConfigure<UpstreamHealthMonitorOptions>(opts => opts.ApplyDefaults());

        return services;
    }

    /// <summary>
    /// Add health checks.
    /// </summary>
    public static IServiceCollection AddGatewayHealthChecks(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var healthChecks = services.AddHealthChecks();

        // Add database health check if configured
        var dbOptions = new DatabaseOptions();
        var providerStr = Environment.GetEnvironmentVariable("DB_PROVIDER") ?? "none";
        dbOptions.Provider = Enum.TryParse<DatabaseProvider>(providerStr, true, out var provider)
            ? provider
            : DatabaseProvider.None;
        dbOptions.ConnectionString = Environment.GetEnvironmentVariable("DB_CONNECTION_STRING");

        if (dbOptions.Provider == DatabaseProvider.None)
        {
            configuration.GetSection(DatabaseOptions.SectionName).Bind(dbOptions);
        }

        // FOSS Gateway ships no DB-specific health checks. Commercial variant
        // binaries register their own (e.g. AddNpgSql, AddSqlServer) at build
        // time.

        return services;
    }

    /// <summary>
    /// Apply database migrations if enabled.
    /// Will not crash the gateway if the database is unreachable.
    /// </summary>
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        var dbOptions = app.Services.GetService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>()?.Value;
        if (dbOptions?.IsEnabled != true || !dbOptions.MigrateOnStartup)
        {
            return;
        }

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var scope = app.Services.CreateScope();
            var context = scope.ServiceProvider.GetService<GatewayDbContext>();
            if (context != null)
            {
                await context.Database.MigrateAsync(cts.Token);
                Log.Information("Database migrations applied successfully");
            }
        }
        catch (OperationCanceledException)
        {
            Log.Warning("Database migration timed out after 30s - gateway will continue without database");
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Database migration failed - gateway will continue without database. " +
                            "Check DB_CONNECTION_STRING and ensure the database is reachable");
        }
    }

    /// <summary>
    /// Initialize the profile calibration SQLite store if profile mode is enabled.
    /// No-op when Enabled=false.
    /// </summary>
    public static async Task InitializeProfileStoreAsync(this WebApplication app)
    {
        var opts = app.Services.GetRequiredService<IOptions<ProfileModeOptions>>().Value;
        if (!opts.Enabled) return;

        var store = app.Services.GetRequiredService<ProfileCalibrationStore>();
        await store.InitializeAsync(app.Lifetime.ApplicationStopping);
        Log.Information("Profile mode active -- calibration store ready");
    }

    public static IServiceCollection AddGatewayTls(this IServiceCollection services, TlsOptions tls)
    {
        if (!tls.Enabled) return services;

        if (tls.IsAcme)
        {
            services.AddLettuceEncrypt(opts =>
            {
                opts.AcceptTermsOfService = true;
                opts.DomainNames = [tls.Domain!];
                if (!string.IsNullOrWhiteSpace(tls.AcmeEmail))
                    opts.EmailAddress = tls.AcmeEmail;
            })
            .PersistDataToDirectory(new DirectoryInfo(tls.AcmeCertStorePath), pfxPassword: null);

            services.AddOptions<KestrelServerOptions>()
                .Configure<ILoggerFactory, IServiceProvider>((kestrelOpts, loggers, sp) =>
                {
                    kestrelOpts.ListenAnyIP(tls.Port, listenOpts =>
                    {
                        listenOpts.UseHttps(https => https.UseLettuceEncrypt(sp));
                    });
                    loggers.CreateLogger("GatewayTls").LogInformation(
                        "HTTPS (ACME) enabled on port {Port} for domain {Domain}", tls.Port, tls.Domain);
                });
        }
        else
        {
            services.AddOptions<KestrelServerOptions>()
                .Configure<ILoggerFactory>((kestrelOpts, loggers) =>
                {
                    var logger = loggers.CreateLogger("GatewayTls");

                    X509Certificate2 cert;
                    try
                    {
                        cert = !string.IsNullOrWhiteSpace(tls.CertKeyPath)
                            ? X509Certificate2.CreateFromPemFile(tls.CertPath!, tls.CertKeyPath)
                            : X509CertificateLoader.LoadPkcs12FromFile(tls.CertPath!, tls.CertPassword);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to load TLS certificate from {Path} — HTTPS disabled", tls.CertPath);
                        return;
                    }

                    kestrelOpts.ListenAnyIP(tls.Port, listenOpts =>
                    {
                        listenOpts.UseHttpsWithTlsCapture(cert, logger);
                    });

                    logger.LogInformation(
                        "HTTPS (cert-from-file) enabled on port {Port}, cert: {Subject}", tls.Port, cert.Subject);
                });
        }

        return services;
    }

    public static TlsOptions ReadTlsOptionsFromEnv() => new()
    {
        Port = GetEnvInt("GATEWAY_HTTPS_PORT", 8443),
        CertPath = Environment.GetEnvironmentVariable("GATEWAY_HTTPS_CERT_PATH"),
        CertKeyPath = Environment.GetEnvironmentVariable("GATEWAY_HTTPS_CERT_KEY_PATH"),
        CertPassword = Environment.GetEnvironmentVariable("GATEWAY_HTTPS_CERT_PASSWORD"),
        Domain = Environment.GetEnvironmentVariable("GATEWAY_HTTPS_DOMAIN"),
        AcmeEmail = Environment.GetEnvironmentVariable("GATEWAY_HTTPS_ACME_EMAIL"),
        AcmeCertStorePath = Environment.GetEnvironmentVariable("GATEWAY_HTTPS_ACME_CERT_STORE") ?? "/app/data/certs",
    };

    private static int GetEnvInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var result) ? result : defaultValue;
    }

    private static bool GetEnvBool(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return bool.TryParse(value, out var result) ? result : defaultValue;
    }
}
