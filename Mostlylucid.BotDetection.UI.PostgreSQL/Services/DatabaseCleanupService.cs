using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Npgsql;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Storage;

/// <summary>
/// Background service that periodically cleans up old detection events.
/// </summary>
public class DatabaseCleanupService : BackgroundService
{
    private readonly PostgreSQLStorageOptions _options;
    private readonly ILogger<DatabaseCleanupService> _logger;

    public DatabaseCleanupService(
        PostgreSQLStorageOptions options,
        ILogger<DatabaseCleanupService> logger)
    {
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_options.RetentionDays <= 0)
        {
            _logger.LogInformation("Automatic cleanup disabled (RetentionDays = 0)");
            return;
        }

        _logger.LogInformation(
            "Database cleanup service started (retention: {Days} days, interval: {Hours}h)",
            _options.RetentionDays,
            _options.CleanupIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromHours(_options.CleanupIntervalHours),
                    stoppingToken);

                await CleanupOldDetectionsAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during database cleanup");
                // Continue running despite errors
            }
        }

        _logger.LogInformation("Database cleanup service stopped");
    }

    private async Task CleanupOldDetectionsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);

            var deleted = await connection.ExecuteScalarAsync<long>(
                "SELECT cleanup_old_detections(@RetentionDays)",
                new { RetentionDays = _options.RetentionDays },
                commandTimeout: _options.CommandTimeoutSeconds);

            if (deleted > 0)
            {
                _logger.LogInformation(
                    "Cleaned up {Count} detection events older than {Days} days",
                    deleted,
                    _options.RetentionDays);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to clean up old detections");
            throw;
        }
    }
}
