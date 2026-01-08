using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.GeoDetection.Data;
using Mostlylucid.GeoDetection.Models;
using Mostlylucid.GeoDetection.Services;

namespace Mostlylucid.GeoDetection.Extensions;

/// <summary>
///     Extension methods for configuring geo-routing services
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Add geo-routing services with configurable provider
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="configureRouting">Configure routing options</param>
    /// <param name="configureProvider">Configure provider options (MaxMind, ip-api.com, etc.)</param>
    /// <param name="configureCache">Configure database caching (disabled by default, uses memory-only)</param>
    public static IServiceCollection AddGeoRouting(
        this IServiceCollection services,
        Action<GeoRoutingOptions>? configureRouting = null,
        Action<GeoLite2Options>? configureProvider = null,
        Action<GeoCacheOptions>? configureCache = null)
    {
        // Configure routing options
        if (configureRouting != null)
            services.Configure(configureRouting);
        else
            services.Configure<GeoRoutingOptions>(options => { });

        // Configure provider options
        if (configureProvider != null)
            services.Configure(configureProvider);
        else
            services.Configure<GeoLite2Options>(options => { });

        // Configure cache options (default: memory-only, no database)
        if (configureCache != null)
            services.Configure(configureCache);
        else
            services.Configure<GeoCacheOptions>(options => options.Enabled = false);

        // Add memory cache for IP lookups
        services.AddMemoryCache();

        // Add HTTP clients
        services.AddHttpClient("GeoLite2", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.Add("User-Agent", "Mostlylucid.GeoDetection/1.0");
        });

        services.AddHttpClient("IpApi", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "Mostlylucid.GeoDetection/1.0");
        });

        services.AddHttpClient("DataHub", client =>
        {
            client.Timeout = TimeSpan.FromMinutes(5);
            client.DefaultRequestHeaders.Add("User-Agent", "Mostlylucid.GeoDetection/1.0");
        });

        // Register provider services
        services.AddSingleton<SimpleGeoLocationService>();
        services.AddSingleton<MaxMindGeoLocationService>();
        services.AddSingleton<IpApiGeoLocationService>();
        services.AddSingleton<DataHubGeoLocationService>();

        // Register the main service based on provider selection
        services.AddSingleton<IGeoLocationService>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<GeoLite2Options>>().Value;
            var cacheOptions = sp.GetRequiredService<IOptions<GeoCacheOptions>>().Value;
            var memoryCache = sp.GetRequiredService<IMemoryCache>();
            var logger = sp.GetRequiredService<ILogger<CachedGeoLocationService>>();

            // Select the provider
            IGeoLocationService provider = options.Provider switch
            {
                GeoProvider.Simple => sp.GetRequiredService<SimpleGeoLocationService>(),
                GeoProvider.MaxMindLocal => sp.GetRequiredService<MaxMindGeoLocationService>(),
                GeoProvider.IpApi => sp.GetRequiredService<IpApiGeoLocationService>(),
                GeoProvider.IpApiCo => sp.GetRequiredService<IpApiGeoLocationService>(),
                GeoProvider.DataHubCsv => sp.GetRequiredService<DataHubGeoLocationService>(),
                _ => sp.GetRequiredService<SimpleGeoLocationService>()
            };

            // If database cache is disabled, return provider directly (uses memory cache only)
            if (!cacheOptions.Enabled) return provider;

            // Wrap with database caching
            var dbContext = sp.GetService<GeoDbContext>();
            return new CachedGeoLocationService(
                provider,
                memoryCache,
                sp.GetRequiredService<IOptions<GeoLite2Options>>(),
                sp.GetRequiredService<IOptions<GeoCacheOptions>>(),
                logger,
                dbContext);
        });

        // Register DataHub as hosted service for startup loading
        services.AddHostedService(sp => sp.GetRequiredService<DataHubGeoLocationService>());

        // Register background update service for MaxMind
        services.AddHostedService<GeoLite2UpdateService>();

        return services;
    }

    /// <summary>
    ///     Add SQLite database caching for geo lookups
    ///     Call this BEFORE AddGeoRouting() if you want database caching
    /// </summary>
    public static IServiceCollection AddGeoCacheDatabase(
        this IServiceCollection services,
        string? connectionString = null)
    {
        services.AddDbContext<GeoDbContext>(options =>
        {
            var connStr = connectionString ?? "Data Source=data/geocache.db";
            options.UseSqlite(connStr);
        });

        // Ensure database is created
        services.AddHostedService<GeoCacheDatabaseInitializer>();

        return services;
    }

    /// <summary>
    ///     Add geo-routing with ip-api.com (free, no account required)
    /// </summary>
    public static IServiceCollection AddGeoRoutingWithIpApi(
        this IServiceCollection services,
        Action<GeoRoutingOptions>? configureRouting = null)
    {
        return services.AddGeoRouting(
            configureRouting,
            options => options.Provider = GeoProvider.IpApi);
    }

    /// <summary>
    ///     Add geo-routing with simple mock implementation (for testing/development)
    /// </summary>
    public static IServiceCollection AddGeoRoutingSimple(
        this IServiceCollection services,
        Action<GeoRoutingOptions>? configure = null)
    {
        return services.AddGeoRouting(
            configure,
            options => options.Provider = GeoProvider.Simple);
    }

    /// <summary>
    ///     Add geo-routing with DataHub CSV database (free, local, no account required)
    ///     Data from: https://datahub.io/core/geoip2-ipv4
    ///     Provides country-level precision with ~27MB CSV, auto-updates weekly
    /// </summary>
    public static IServiceCollection AddGeoRoutingWithDataHub(
        this IServiceCollection services,
        Action<GeoRoutingOptions>? configureRouting = null)
    {
        return services.AddGeoRouting(
            configureRouting,
            options => options.Provider = GeoProvider.DataHubCsv);
    }

    /// <summary>
    ///     Configure site to only allow specific countries
    /// </summary>
    public static IServiceCollection RestrictSiteToCountries(
        this IServiceCollection services,
        params string[] countryCodes)
    {
        return services.AddGeoRouting(options =>
        {
            options.AllowedCountries = countryCodes;
            options.Enabled = true;
        });
    }

    /// <summary>
    ///     Configure site to block specific countries
    /// </summary>
    public static IServiceCollection BlockCountries(
        this IServiceCollection services,
        params string[] countryCodes)
    {
        return services.AddGeoRouting(options =>
        {
            options.BlockedCountries = countryCodes;
            options.Enabled = true;
        });
    }
}

/// <summary>
///     Background service to initialize the geo cache database
/// </summary>
internal class GeoCacheDatabaseInitializer : IHostedService
{
    private readonly ILogger<GeoCacheDatabaseInitializer> _logger;
    private readonly IServiceProvider _serviceProvider;

    public GeoCacheDatabaseInitializer(IServiceProvider serviceProvider, ILogger<GeoCacheDatabaseInitializer> logger)
    {
        _serviceProvider = serviceProvider;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        using var scope = _serviceProvider.CreateScope();
        var dbContext = scope.ServiceProvider.GetService<GeoDbContext>();

        if (dbContext != null)
            try
            {
                await dbContext.Database.EnsureCreatedAsync(cancellationToken);
                _logger.LogInformation("Geo cache database initialized");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to initialize geo cache database");
            }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}