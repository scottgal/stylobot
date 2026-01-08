using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Common.Services;

/// <summary>
///     Base class for services that need to periodically update data (databases, caches, etc.)
/// </summary>
public abstract class PeriodicUpdateService : BackgroundService
{
    private readonly ILogger _logger;
    private readonly string _serviceName;

    protected PeriodicUpdateService(ILogger logger, string? serviceName = null)
    {
        _logger = logger;
        _serviceName = serviceName ?? GetType().Name;
    }

    /// <summary>
    ///     How often to check for updates
    /// </summary>
    protected abstract TimeSpan UpdateInterval { get; }

    /// <summary>
    ///     Whether to run an update check on startup
    /// </summary>
    protected virtual bool UpdateOnStartup => true;

    /// <summary>
    ///     Get the last update time (return null to force update)
    /// </summary>
    protected abstract Task<DateTime?> GetLastUpdateTimeAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Perform the update
    /// </summary>
    protected abstract Task PerformUpdateAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Initialize the service (called once on startup before update loop)
    /// </summary>
    protected virtual Task InitializeAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("{ServiceName} starting", _serviceName);

        try
        {
            await InitializeAsync(stoppingToken);

            if (UpdateOnStartup) await CheckAndUpdateAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
                try
                {
                    await Task.Delay(UpdateInterval, stoppingToken);
                    await CheckAndUpdateAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "{ServiceName} error during update cycle", _serviceName);
                    // Continue running despite errors
                }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ServiceName} fatal error", _serviceName);
        }

        _logger.LogInformation("{ServiceName} stopped", _serviceName);
    }

    private async Task CheckAndUpdateAsync(CancellationToken cancellationToken)
    {
        try
        {
            var lastUpdate = await GetLastUpdateTimeAsync(cancellationToken);

            if (lastUpdate == null || DateTime.UtcNow - lastUpdate.Value >= UpdateInterval)
            {
                _logger.LogInformation("{ServiceName} performing update (last update: {LastUpdate})",
                    _serviceName, lastUpdate?.ToString("u") ?? "never");

                await PerformUpdateAsync(cancellationToken);

                _logger.LogInformation("{ServiceName} update completed", _serviceName);
            }
            else
            {
                _logger.LogDebug("{ServiceName} skipping update (last update: {LastUpdate}, next in: {NextIn})",
                    _serviceName, lastUpdate.Value.ToString("u"),
                    UpdateInterval - (DateTime.UtcNow - lastUpdate.Value));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{ServiceName} failed to check/perform update", _serviceName);
        }
    }
}