using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.SignatureStore.Repositories;

namespace Mostlylucid.BotDetection.SignatureStore.Services;

/// <summary>
/// Background service for cleaning up expired signatures.
/// Runs periodically to delete signatures based on TTL and retention policies.
/// </summary>
public class SignatureCleanupService : BackgroundService
{
    private readonly IServiceProvider _serviceProvider;
    private readonly SignatureStoreOptions _options;
    private readonly ILogger<SignatureCleanupService> _logger;

    public SignatureCleanupService(
        IServiceProvider serviceProvider,
        SignatureStoreOptions options,
        ILogger<SignatureCleanupService> logger)
    {
        _serviceProvider = serviceProvider;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "SignatureCleanupService started (interval: {Hours} hours)",
            _options.CleanupIntervalHours);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(
                    TimeSpan.FromHours(_options.CleanupIntervalHours),
                    stoppingToken);

                await PerformCleanupAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Service is stopping
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during signature cleanup");
            }
        }

        _logger.LogInformation("SignatureCleanupService stopped");
    }

    private async Task PerformCleanupAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("Starting signature cleanup...");

        using var scope = _serviceProvider.CreateScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISignatureRepository>();

        var totalDeleted = 0;

        // Delete expired signatures (based on ExpiresAt column)
        var expiredDeleted = await repository.DeleteExpiredAsync(cancellationToken);
        totalDeleted += expiredDeleted;

        _logger.LogInformation("Deleted {Count} expired signatures", expiredDeleted);

        // Delete old signatures based on retention policy
        if (_options.RetentionDays > 0)
        {
            var cutoffDate = DateTime.UtcNow.AddDays(-_options.RetentionDays);
            var oldDeleted = await repository.DeleteOlderThanAsync(cutoffDate, cancellationToken);
            totalDeleted += oldDeleted;

            _logger.LogInformation(
                "Deleted {Count} signatures older than {CutoffDate}",
                oldDeleted, cutoffDate);
        }

        // Enforce max signature limit
        if (_options.MaxSignatures > 0)
        {
            var stats = await repository.GetStatsAsync(cancellationToken);

            if (stats.TotalSignatures > _options.MaxSignatures)
            {
                var excessCount = stats.TotalSignatures - _options.MaxSignatures;
                var cutoffDate = stats.OldestSignature?.AddDays(1) ?? DateTime.UtcNow;

                _logger.LogInformation(
                    "Max signatures ({Max}) exceeded by {Excess}, deleting oldest",
                    _options.MaxSignatures, excessCount);

                var limitDeleted = await repository.DeleteOlderThanAsync(cutoffDate, cancellationToken);
                totalDeleted += limitDeleted;

                _logger.LogInformation("Deleted {Count} signatures to enforce limit", limitDeleted);
            }
        }

        _logger.LogInformation("Cleanup complete. Total deleted: {Count}", totalDeleted);
    }
}
