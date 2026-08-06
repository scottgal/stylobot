using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Microsoft.Data.Sqlite;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Periodic cleanup of raw detection rows past the configured retention window.
///     The <c>dashboard_detections</c> table accumulates per-request data indefinitely;
///     without this, millions of stale rows degrade query performance and waste disk.
///     Runs once per hour — cheap, low-priority maintenance.
/// </summary>
public sealed class DetectionRetentionPruner : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<DetectionRetentionPruner> _logger;
    private readonly TimeSpan _retention;
    private readonly TimeSpan _interval;

    public DetectionRetentionPruner(
        IServiceScopeFactory scopeFactory,
        IOptions<StyloBotDashboardOptions> options,
        ILogger<DetectionRetentionPruner> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _retention = options.Value.DetectionRetention;
        _interval = TimeSpan.FromHours(1);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Staggered start: wait 5 minutes before first prune so startup isn't
        // competing with the initial warm tick.
        await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PruneAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "DetectionRetentionPruner: prune cycle failed");
            }

            await Task.Delay(_interval, stoppingToken);
        }
    }

    private async Task PruneAsync(CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<StyloBotDashboardOptions>>();
        var retention = options.Value.DetectionRetention;
        var cutoff = DateTime.UtcNow - retention;

        // Resolve the connection string from the event store's own connection.
        var eventStore = scope.ServiceProvider.GetService<IDashboardEventStore>();
        if (eventStore is not SqliteDashboardEventStore sqliteStore)
        {
            // Not a SQLite store — skip (commercial Postgres handles this separately).
            return;
        }

        try
        {
            var connStr = sqliteStore.ConnectionString;
            await using var conn = new SqliteConnection(connStr);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM detections WHERE timestamp < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
            var deleted = await cmd.ExecuteNonQueryAsync(ct);

            if (deleted > 0)
                _logger.LogInformation("DetectionRetentionPruner: pruned {Count} row(s) older than {Cutoff:yyyy-MM-dd}",
                    deleted, cutoff);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "DetectionRetentionPruner: failed to prune detections");
        }
    }
}
