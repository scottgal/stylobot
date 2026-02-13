using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Stylobot.Gateway.Configuration;
using Stylobot.Gateway.Services;
using Yarp.ReverseProxy.Configuration;

namespace Stylobot.Gateway.Endpoints;

/// <summary>
/// Admin API endpoints for gateway management.
/// </summary>
public static class AdminEndpoints
{
    /// <summary>
    /// Map all admin endpoints.
    /// </summary>
    public static WebApplication MapAdminEndpoints(this WebApplication app)
    {
        MapAdminEndpointsInternal(app);
        return app;
    }

    /// <summary>
    /// Map all admin endpoints (for testing with IEndpointRouteBuilder).
    /// </summary>
    public static IEndpointRouteBuilder MapAdminEndpoints(this IEndpointRouteBuilder endpoints)
    {
        MapAdminEndpointsInternal(endpoints);
        return endpoints;
    }

    private static void MapAdminEndpointsInternal(IEndpointRouteBuilder app)
    {
        var adminPath = Environment.GetEnvironmentVariable("ADMIN_BASE_PATH") ?? "/admin";

        var group = app.MapGroup(adminPath)
            .WithTags("Admin");

        // Liveness probe - lightweight, no DB hit (use for Docker HEALTHCHECK)
        group.MapGet("/alive", () => Results.Ok(new { status = "alive" }))
            .WithName("GetAlive")
            .WithSummary("Lightweight liveness probe (no database check)");

        // Readiness/health check (hits database)
        group.MapGet("/health", GetHealth)
            .WithName("GetHealth")
            .WithSummary("Full health check endpoint (includes database)");

        // Configuration
        group.MapGet("/config/effective", GetEffectiveConfig)
            .WithName("GetEffectiveConfig")
            .WithSummary("Current merged effective configuration");

        group.MapGet("/config/sources", GetConfigSources)
            .WithName("GetConfigSources")
            .WithSummary("Active configuration sources");

        // File system inspection
        group.MapGet("/fs", GetFileSystems)
            .WithName("GetFileSystems")
            .WithSummary("List logical directories");

        group.MapGet("/fs/{logical}", GetFileSystemContents)
            .WithName("GetFileSystemContents")
            .WithSummary("List contents of a logical directory");

        // Routes info
        group.MapGet("/routes", GetRoutes)
            .WithName("GetRoutes")
            .WithSummary("Current YARP routes");

        group.MapGet("/clusters", GetClusters)
            .WithName("GetClusters")
            .WithSummary("Current YARP clusters");

        // Metrics
        group.MapGet("/metrics", GetMetrics)
            .WithName("GetMetrics")
            .WithSummary("Gateway metrics");
    }

    private static async Task<IResult> GetHealth(
        HealthCheckService healthCheckService,
        GatewayMetrics metrics,
        IOptions<DatabaseOptions> dbOptions,
        IProxyConfigProvider configProvider)
    {
        var report = await healthCheckService.CheckHealthAsync();
        var config = configProvider.GetConfig();

        var response = new
        {
            status = report.Status == HealthStatus.Healthy ? "ok" : report.Status.ToString().ToLower(),
            uptimeSeconds = (long)metrics.Uptime.TotalSeconds,
            routesConfigured = config.Routes.Count,
            clustersConfigured = config.Clusters.Count,
            mode = GetMode(config),
            db = dbOptions.Value.IsEnabled
                ? (report.Entries.Any(e => e.Key.Contains("sql", StringComparison.OrdinalIgnoreCase) ||
                                           e.Key.Contains("postgres", StringComparison.OrdinalIgnoreCase))
                    ? report.Entries.Values.All(e => e.Status == HealthStatus.Healthy)
                        ? "connected"
                        : "error"
                    : "pending")
                : "disabled",
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString().ToLower(),
                duration = e.Value.Duration.TotalMilliseconds
            })
        };

        return Results.Ok(response);
    }

    private static IResult GetEffectiveConfig(
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<DatabaseOptions> dbOptions,
        IProxyConfigProvider configProvider,
        ConfigurationService configService)
    {
        var config = configProvider.GetConfig();

        var response = new
        {
            gateway = new
            {
                httpPort = gatewayOptions.Value.HttpPort,
                adminBasePath = gatewayOptions.Value.AdminBasePath,
                hasAdminSecret = !string.IsNullOrEmpty(gatewayOptions.Value.AdminSecret),
                defaultUpstream = gatewayOptions.Value.DefaultUpstream,
                logLevel = gatewayOptions.Value.LogLevel
            },
            database = new
            {
                provider = dbOptions.Value.Provider.ToString(),
                enabled = dbOptions.Value.IsEnabled,
                migrateOnStartup = dbOptions.Value.MigrateOnStartup
            },
            yarp = new
            {
                routeCount = config.Routes.Count,
                clusterCount = config.Clusters.Count,
                configFile = GatewayPaths.YarpConfig,
                configFileExists = File.Exists(GatewayPaths.YarpConfig)
            },
            paths = GatewayPaths.All
        };

        return Results.Ok(response);
    }

    private static IResult GetConfigSources(
        IOptions<GatewayOptions> gatewayOptions,
        IOptions<DatabaseOptions> dbOptions)
    {
        var sources = new List<object>
        {
            new { source = "built-in", active = true, precedence = 1 }
        };

        // Check for config files
        var appSettingsPath = Path.Combine(GatewayPaths.Config, "appsettings.json");
        sources.Add(new
        {
            source = "file:appsettings.json",
            active = File.Exists(appSettingsPath),
            path = appSettingsPath,
            precedence = 2
        });

        sources.Add(new
        {
            source = "file:yarp.json",
            active = File.Exists(GatewayPaths.YarpConfig),
            path = GatewayPaths.YarpConfig,
            precedence = 2
        });

        // Environment variables
        var envVars = new[] { "GATEWAY_HTTP_PORT", "DEFAULT_UPSTREAM", "ADMIN_SECRET", "DB_PROVIDER" };
        var activeEnvVars = envVars.Where(v => !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(v))).ToList();
        sources.Add(new
        {
            source = "environment",
            active = activeEnvVars.Any(),
            variables = activeEnvVars,
            precedence = 3
        });

        // Database
        sources.Add(new
        {
            source = "database",
            active = dbOptions.Value.IsEnabled,
            provider = dbOptions.Value.Provider.ToString(),
            precedence = 4
        });

        return Results.Ok(new { sources, effectivePrecedence = "environment > file > built-in" });
    }

    private static IResult GetFileSystems()
    {
        var directories = GatewayPaths.All.Select(kv => new
        {
            name = kv.Key,
            path = kv.Value,
            exists = Directory.Exists(kv.Value),
            writable = IsDirectoryWritable(kv.Value)
        });

        return Results.Ok(new { directories });
    }

    private static IResult GetFileSystemContents(string logical)
    {
        if (!GatewayPaths.All.TryGetValue(logical, out var path))
        {
            return Results.NotFound(new { error = $"Unknown logical directory: {logical}" });
        }

        if (!Directory.Exists(path))
        {
            return Results.Ok(new
            {
                logicalName = logical,
                path,
                exists = false,
                entries = Array.Empty<object>()
            });
        }

        var entries = new List<object>();

        foreach (var dir in Directory.GetDirectories(path))
        {
            var info = new DirectoryInfo(dir);
            entries.Add(new
            {
                name = info.Name,
                type = "directory",
                modified = info.LastWriteTimeUtc
            });
        }

        foreach (var file in Directory.GetFiles(path))
        {
            var info = new FileInfo(file);
            entries.Add(new
            {
                name = info.Name,
                type = "file",
                size = info.Length,
                modified = info.LastWriteTimeUtc
            });
        }

        return Results.Ok(new
        {
            logicalName = logical,
            path,
            exists = true,
            entries = entries.OrderBy(e => ((dynamic)e).type).ThenBy(e => ((dynamic)e).name)
        });
    }

    private static IResult GetRoutes(IProxyConfigProvider configProvider)
    {
        var config = configProvider.GetConfig();

        var routes = config.Routes.Select(r => new
        {
            routeId = r.RouteId,
            clusterId = r.ClusterId,
            match = new
            {
                path = r.Match?.Path,
                hosts = r.Match?.Hosts,
                methods = r.Match?.Methods,
                headers = r.Match?.Headers?.Select(h => new { h.Name, h.Values })
            },
            order = r.Order,
            authorizationPolicy = r.AuthorizationPolicy,
            rateLimiterPolicy = r.RateLimiterPolicy
        });

        return Results.Ok(new
        {
            count = config.Routes.Count,
            routes
        });
    }

    private static IResult GetClusters(IProxyConfigProvider configProvider)
    {
        var config = configProvider.GetConfig();

        var clusters = config.Clusters.Select(c => new
        {
            clusterId = c.ClusterId,
            loadBalancingPolicy = c.LoadBalancingPolicy,
            destinations = c.Destinations?.Select(d => new
            {
                name = d.Key,
                address = d.Value.Address,
                health = d.Value.Health
            }),
            healthCheck = c.HealthCheck != null ? new
            {
                passive = c.HealthCheck.Passive?.Enabled,
                active = c.HealthCheck.Active?.Enabled
            } : null
        });

        return Results.Ok(new
        {
            count = config.Clusters.Count,
            clusters
        });
    }

    private static IResult GetMetrics(GatewayMetrics metrics)
    {
        return Results.Ok(new
        {
            uptimeSeconds = (long)metrics.Uptime.TotalSeconds,
            requestsTotal = metrics.RequestsTotal,
            requestsPerSecond = metrics.RequestsPerSecond,
            errorsTotal = metrics.ErrorsTotal,
            activeConnections = metrics.ActiveConnections,
            bytesIn = metrics.BytesIn,
            bytesOut = metrics.BytesOut
        });
    }

    private static string GetMode(IProxyConfig config)
    {
        if (config.Routes.Count == 0)
            return "zero-config";

        if (config.Routes.Count == 1 && config.Routes[0].RouteId == "default-catch-all")
            return "default-upstream";

        return "configured";
    }

    private static bool IsDirectoryWritable(string path)
    {
        if (!Directory.Exists(path))
            return false;

        try
        {
            var testFile = Path.Combine(path, $".write-test-{Guid.NewGuid()}");
            File.WriteAllText(testFile, "test");
            File.Delete(testFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
