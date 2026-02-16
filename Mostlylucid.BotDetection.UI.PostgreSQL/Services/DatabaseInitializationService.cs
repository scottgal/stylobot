using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Npgsql;
using System.Reflection;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Storage;

/// <summary>
/// Background service that initializes the PostgreSQL database schema on startup.
/// </summary>
public class DatabaseInitializationService : IHostedService
{
    private readonly PostgreSQLStorageOptions _options;
    private readonly ILogger<DatabaseInitializationService> _logger;

    public DatabaseInitializationService(
        PostgreSQLStorageOptions options,
        ILogger<DatabaseInitializationService> _logger)
    {
        _options = options;
        this._logger = _logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.AutoInitializeSchema)
        {
            _logger.LogInformation("Schema auto-initialization is disabled");
            return;
        }

        try
        {
            _logger.LogInformation("Initializing PostgreSQL database schema...");

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Mostlylucid.BotDetection.UI.PostgreSQL.Schema.schema.sql";

            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _logger.LogWarning("Schema resource not found: {ResourceName}", resourceName);
                return;
            }

            using var reader = new StreamReader(stream);
            var schema = await reader.ReadToEndAsync(cancellationToken);

            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = new NpgsqlCommand(schema, connection);
            command.CommandTimeout = _options.CommandTimeoutSeconds * 5; // Longer timeout for schema creation

            await command.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("PostgreSQL database schema initialized successfully");

            // Apply TimescaleDB enhancements if enabled (non-fatal — app works without them)
            if (_options.EnableTimescaleDB)
            {
                await ApplyTimescaleDBEnhancementsAsync(connection, cancellationToken);
            }
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            _logger.LogWarning(ex,
                "Failed to connect to PostgreSQL — dashboard persistence disabled. " +
                "The app will continue with in-memory storage. " +
                "Set a valid connection string or start PostgreSQL to enable persistence.");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to initialize PostgreSQL database schema");
            throw; // Non-connectivity errors (e.g. bad SQL) should still crash
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private async Task ApplyTimescaleDBEnhancementsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogInformation("Applying TimescaleDB enhancements...");

            var assembly = Assembly.GetExecutingAssembly();
            var resourceName = "Mostlylucid.BotDetection.UI.PostgreSQL.Schema.timescaledb_enhancements.sql";

            await using var stream = assembly.GetManifestResourceStream(resourceName);
            if (stream == null)
            {
                _logger.LogWarning("TimescaleDB enhancements resource not found: {ResourceName}", resourceName);
                return;
            }

            using var reader = new StreamReader(stream);
            var enhancements = await reader.ReadToEndAsync(cancellationToken);

            // TimescaleDB continuous aggregates (CREATE MATERIALIZED VIEW ... WITH (timescaledb.continuous))
            // cannot run inside a transaction. Npgsql batches multi-statement commands in an implicit transaction.
            // Split the SQL on the -- SPLIT_BATCH marker so each section runs as its own top-level command.
            var batches = enhancements.Split(
                ["-- SPLIT_BATCH"],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            foreach (var batch in batches)
            {
                if (string.IsNullOrWhiteSpace(batch)) continue;

                try
                {
                    await using var command = new NpgsqlCommand(batch, connection);
                    command.CommandTimeout = _options.CommandTimeoutSeconds * 10;
                    await command.ExecuteNonQueryAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    // Log and continue — individual batch failures shouldn't block remaining batches
                    _logger.LogWarning(ex, "TimescaleDB batch failed (non-fatal): {Message}", ex.Message);
                }
            }

            _logger.LogInformation(
                "TimescaleDB enhancements applied successfully (hypertables, compression, continuous aggregates)");
        }
        catch (Exception ex)
        {
            // Non-fatal: TimescaleDB enhancements are optional optimizations.
            // The app works fine with plain PostgreSQL tables if these fail.
            _logger.LogWarning(ex,
                "TimescaleDB enhancements could not be applied. " +
                "The app will continue with standard PostgreSQL tables. " +
                "Ensure TimescaleDB extension is installed for optimal performance.");
        }
    }
}
