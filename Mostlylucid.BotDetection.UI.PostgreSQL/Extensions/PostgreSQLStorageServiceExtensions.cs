using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Mostlylucid.BotDetection.UI.PostgreSQL.Storage;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Extensions;

/// <summary>
/// Service registration extensions for PostgreSQL storage provider.
/// PostgreSQL takes priority over SQLite when configured.
/// </summary>
public static class PostgreSQLStorageServiceExtensions
{
    /// <summary>
    /// Adds PostgreSQL storage for Stylobot Dashboard AND bot detection data.
    /// Replaces in-memory/SQLite storage with durable PostgreSQL backend.
    /// PostgreSQL is always preferred when this method is called.
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

        // Replace in-memory event store with PostgreSQL (dashboard)
        // Remove any existing registration first to ensure PostgreSQL wins
        services.RemoveAll<IDashboardEventStore>();
        services.AddSingleton<IDashboardEventStore, PostgreSQLDashboardEventStore>();

        // Replace SQLite stores with PostgreSQL (bot detection data)
        // These must be called AFTER AddBotDetection(), so we remove existing registrations
        services.RemoveAll<ILearnedPatternStore>();
        services.AddSingleton<ILearnedPatternStore, PostgreSQLLearnedPatternStore>();

        services.RemoveAll<IWeightStore>();
        services.AddSingleton<IWeightStore, PostgreSQLWeightStore>();

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
