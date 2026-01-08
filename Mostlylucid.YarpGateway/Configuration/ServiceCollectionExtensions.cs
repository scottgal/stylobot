using Microsoft.EntityFrameworkCore;
using Mostlylucid.YarpGateway.Data;
using Mostlylucid.YarpGateway.Services;
using Mostlylucid.YarpGateway.Transforms;
using Yarp.ReverseProxy.Configuration;

namespace Mostlylucid.YarpGateway.Configuration;

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

        services.AddDbContext<GatewayDbContext>(options =>
        {
            switch (dbOptions.Provider)
            {
                case DatabaseProvider.Postgres:
                    options.UseNpgsql(dbOptions.ConnectionString);
                    break;
                case DatabaseProvider.SqlServer:
                    options.UseSqlServer(dbOptions.ConnectionString);
                    break;
            }
        });

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
            .AddTransforms(builderContext =>
            {
                // Add TLS/TCP/HTTP2 fingerprinting headers to all proxied requests
                // These headers are consumed by bot detection contributors:
                // - TlsFingerprintContributor
                // - TcpIpFingerprintContributor
                // - Http2FingerprintContributor
                builderContext.AddTlsFingerprintingHeaders();

                // Add bot detection headers transform based on demo mode
                builderContext.AddDemoModeTransform(configuration);
            });

        // Check for DEFAULT_UPSTREAM first (highest priority - zero-config mode)
        var defaultUpstream = Environment.GetEnvironmentVariable("DEFAULT_UPSTREAM");
        if (!string.IsNullOrWhiteSpace(defaultUpstream))
        {
            Console.WriteLine($"[YarpGateway] Using DEFAULT_UPSTREAM: {defaultUpstream}");
            // Create in-memory config provider for catch-all routing
            services.AddSingleton<IProxyConfigProvider>(sp =>
                new DefaultUpstreamConfigProvider(defaultUpstream));
            return services;
        }

        // Try to load from YARP config file
        var yarpConfigPath = GatewayPaths.YarpConfig;
        if (File.Exists(yarpConfigPath))
        {
            Console.WriteLine($"[YarpGateway] Loading routes from config file: {yarpConfigPath}");
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

        if (dbOptions.IsEnabled)
        {
            switch (dbOptions.Provider)
            {
                case DatabaseProvider.Postgres:
                    healthChecks.AddNpgSql(dbOptions.ConnectionString!, name: "postgres");
                    break;
                case DatabaseProvider.SqlServer:
                    healthChecks.AddSqlServer(dbOptions.ConnectionString!, name: "sqlserver");
                    break;
            }
        }

        return services;
    }

    /// <summary>
    /// Apply database migrations if enabled.
    /// </summary>
    public static async Task ApplyMigrationsAsync(this WebApplication app)
    {
        var dbOptions = app.Services.GetService<Microsoft.Extensions.Options.IOptions<DatabaseOptions>>()?.Value;
        if (dbOptions?.IsEnabled != true || !dbOptions.MigrateOnStartup)
        {
            return;
        }

        using var scope = app.Services.CreateScope();
        var context = scope.ServiceProvider.GetService<GatewayDbContext>();
        if (context != null)
        {
            await context.Database.MigrateAsync();
        }
    }

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
