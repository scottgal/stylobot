using Microsoft.Extensions.DependencyInjection;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Mostlylucid.BotDetection.UI.PostgreSQL.Storage;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Extensions;

/// <summary>
/// Service registration extensions for PostgreSQL storage provider.
/// </summary>
public static class PostgreSQLStorageServiceExtensions
{
    /// <summary>
    /// Adds PostgreSQL storage for Stylobot Dashboard.
    /// Replaces in-memory storage with durable PostgreSQL backend.
    /// </summary>
    /// <param name="services">Service collection</param>
    /// <param name="connectionString">PostgreSQL connection string</param>
    /// <param name="configure">Optional configuration</param>
    /// <returns>Service collection for chaining</returns>
    public static IServiceCollection AddStyloBotPostgreSQL(
        this IServiceCollection services,
        string connectionString,
        Action<PostgreSQLStorageOptions>? configure = null)
    {
        var options = new PostgreSQLStorageOptions
        {
            ConnectionString = connectionString
        };

        configure?.Invoke(options);

        services.AddSingleton(options);

        // Replace in-memory event store with PostgreSQL
        services.AddSingleton<IDashboardEventStore, PostgreSQLDashboardEventStore>();

        // Add schema initialization service
        services.AddHostedService<DatabaseInitializationService>();

        // Add cleanup service if enabled
        if (options.EnableAutomaticCleanup)
        {
            services.AddHostedService<DatabaseCleanupService>();
        }

        return services;
    }

    /// <summary>
    /// Adds PostgreSQL storage with options configuration.
    /// </summary>
    public static IServiceCollection AddStyloBotPostgreSQL(
        this IServiceCollection services,
        Action<PostgreSQLStorageOptions> configure)
    {
        var options = new PostgreSQLStorageOptions();
        configure(options);

        if (string.IsNullOrWhiteSpace(options.ConnectionString))
        {
            throw new ArgumentException("ConnectionString is required");
        }

        return services.AddStyloBotPostgreSQL(options.ConnectionString, configure);
    }
}
