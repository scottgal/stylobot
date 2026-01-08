using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that automatically updates bot detection lists.
///     Designed to be fail-safe: all failures are logged but never crash the application.
/// </summary>
public class BotListUpdateService : BackgroundService
{
    private readonly IBotListDatabase _database;
    private readonly ILogger<BotListUpdateService> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;
    private readonly ICompiledPatternCache? _patternCache;
    private int _consecutiveFailures;
    private DateTime? _lastSuccessfulUpdate;

    public BotListUpdateService(
        IBotListDatabase database,
        ILogger<BotListUpdateService> logger,
        IOptions<BotDetectionOptions> options,
        ICompiledPatternCache? patternCache = null,
        BotDetectionMetrics? metrics = null)
    {
        _database = database;
        _logger = logger;
        _options = options.Value;
        _patternCache = patternCache;
        _metrics = metrics;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Check if background updates are enabled
        if (!_options.EnableBackgroundUpdates)
        {
            _logger.LogInformation(
                "Bot list background updates are disabled. Lists will only be loaded once at startup");
            return;
        }

#pragma warning disable CS0618 // Type or member is obsolete
        _logger.LogInformation(
            "Bot list update service started. Update interval: {Hours}h, Check interval: {Minutes}m",
            _options.UpdateIntervalHours,
            _options.UpdateCheckIntervalMinutes);
#pragma warning restore CS0618 // Type or member is obsolete

        // Delay startup to avoid slowing down application startup
        if (_options.StartupDelaySeconds > 0)
        {
            _logger.LogDebug("Delaying bot list initialization by {Seconds} seconds", _options.StartupDelaySeconds);
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        // Initialize database on startup (fail-safe)
        await InitializeDatabaseSafeAsync(stoppingToken);

        // Main update loop
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await CheckAndUpdateListsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                // Catch-all: log but never crash
                _consecutiveFailures++;
                _logger.LogError(ex,
                    "Unexpected error in bot list update service (failure #{FailureCount}). " +
                    "Service will continue running",
                    _consecutiveFailures);
            }

            // Wait for next check
            try
            {
                var delay = CalculateNextCheckDelay();
                _logger.LogDebug("Next bot list check in {Minutes} minutes", delay.TotalMinutes);
                await Task.Delay(delay, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Bot list update service stopped");
    }

    private async Task InitializeDatabaseSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Initializing bot detection database...");
            await _database.InitializeAsync(cancellationToken);
            _logger.LogInformation("Bot detection database initialized successfully");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Log error but continue - the service can run with stale/no data
            _logger.LogError(ex,
                "Failed to initialize bot detection database. " +
                "Bot detection will continue with limited functionality until next successful update");
        }
    }

    private async Task CheckAndUpdateListsAsync(CancellationToken cancellationToken)
    {
        try
        {
#pragma warning disable CS0618 // Type or member is obsolete
            // Check if update is needed
            var lastUpdate = await _database.GetLastUpdateTimeAsync("bot_patterns", cancellationToken);
            var updateInterval = TimeSpan.FromHours(_options.UpdateIntervalHours);
#pragma warning restore CS0618 // Type or member is obsolete

            if (lastUpdate == null)
            {
                _logger.LogInformation("No previous bot list update found. Starting initial update...");
                await PerformUpdateWithRetriesAsync(cancellationToken);
            }
            else if (DateTime.UtcNow - lastUpdate.Value >= updateInterval)
            {
                _logger.LogInformation(
                    "Bot lists are stale (last update: {LastUpdate:u}). Starting update...",
                    lastUpdate.Value);
                await PerformUpdateWithRetriesAsync(cancellationToken);
            }
            else
            {
                var nextUpdate = lastUpdate.Value.Add(updateInterval);
                var timeUntilUpdate = nextUpdate - DateTime.UtcNow;

                if (_options.LogPerformanceMetrics)
                    _logger.LogDebug(
                        "Bot lists are up to date (last update: {LastUpdate:u}). Next update in {Hours:F1} hours",
                        lastUpdate.Value, timeUntilUpdate.TotalHours);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogWarning(ex,
                "Failed to check bot list update status (failure #{FailureCount}). Will retry later",
                _consecutiveFailures);
        }
    }

    private async Task PerformUpdateWithRetriesAsync(CancellationToken cancellationToken)
    {
        var maxRetries = _options.MaxDownloadRetries;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
            try
            {
                _logger.LogDebug("Starting bot list update (attempt {Attempt}/{MaxRetries})", attempt, maxRetries);

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(_options.ListDownloadTimeoutSeconds * 3)); // Total timeout

                await _database.UpdateListsAsync(timeoutCts.Token);

                // Update the compiled pattern cache with new patterns
                await UpdatePatternCacheAsync(cancellationToken);

                _lastSuccessfulUpdate = DateTime.UtcNow;
                _consecutiveFailures = 0;

                _logger.LogInformation("Bot list update completed successfully");
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _consecutiveFailures++;

                if (attempt < maxRetries)
                {
                    var retryDelay = TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5); // Exponential backoff
                    _logger.LogWarning(ex,
                        "Bot list update attempt {Attempt}/{MaxRetries} failed. Retrying in {Seconds} seconds...",
                        attempt, maxRetries, retryDelay.TotalSeconds);

                    await Task.Delay(retryDelay, cancellationToken);
                }
                else
                {
                    _logger.LogError(ex,
                        "Bot list update failed after {MaxRetries} attempts. " +
                        "Will retry at next scheduled check. Bot detection continues with existing data",
                        maxRetries);
                }
            }
    }

    private async Task UpdatePatternCacheAsync(CancellationToken cancellationToken)
    {
        if (_patternCache == null)
            return;

        try
        {
            // Get patterns from database and compile them
            var patterns = await _database.GetBotPatternsAsync(cancellationToken);
            _patternCache.UpdateDownloadedPatterns(patterns);

            // Get IP ranges from database and parse them
            var ipRanges = await _database.GetDatacenterIpRangesAsync(cancellationToken);
            _patternCache.UpdateDownloadedCidrRanges(ipRanges);

            // Update metrics
            _metrics?.UpdatePatternCount(_patternCache.DownloadedPatterns.Count);
            _metrics?.UpdateCidrCount(_patternCache.DownloadedCidrRanges.Count);

            _logger.LogInformation(
                "Pattern cache updated: {PatternCount} compiled patterns, {CidrCount} parsed CIDR ranges",
                _patternCache.DownloadedPatterns.Count,
                _patternCache.DownloadedCidrRanges.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update pattern cache. Detection will use database queries instead");
        }
    }

    private TimeSpan CalculateNextCheckDelay()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        var baseDelay = TimeSpan.FromMinutes(_options.UpdateCheckIntervalMinutes);

        // If we've had consecutive failures, use exponential backoff
        if (_consecutiveFailures > 0)
        {
            var backoffMinutes = Math.Min(
                _options.UpdateCheckIntervalMinutes * Math.Pow(1.5, _consecutiveFailures),
                _options.UpdateIntervalHours * 60 // Cap at update interval
            );
#pragma warning restore CS0618 // Type or member is obsolete

            var backoffDelay = TimeSpan.FromMinutes(backoffMinutes);
            _logger.LogDebug(
                "Using exponential backoff due to {FailureCount} consecutive failures. " +
                "Next check in {Minutes:F0} minutes instead of {BaseMinutes}",
                _consecutiveFailures, backoffDelay.TotalMinutes, baseDelay.TotalMinutes);

            return backoffDelay;
        }

        return baseDelay;
    }

    /// <summary>
    ///     Gets the current status of the update service.
    /// </summary>
    public BotListUpdateStatus GetStatus()
    {
#pragma warning disable CS0618 // Type or member is obsolete
        return new BotListUpdateStatus
        {
            IsEnabled = _options.EnableBackgroundUpdates,
            LastSuccessfulUpdate = _lastSuccessfulUpdate,
            ConsecutiveFailures = _consecutiveFailures,
            UpdateIntervalHours = _options.UpdateIntervalHours,
            CheckIntervalMinutes = _options.UpdateCheckIntervalMinutes
#pragma warning restore CS0618 // Type or member is obsolete
        };
    }
}

/// <summary>
///     Status information for the bot list update service.
/// </summary>
public class BotListUpdateStatus
{
    public bool IsEnabled { get; set; }
    public DateTime? LastSuccessfulUpdate { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int UpdateIntervalHours { get; set; }
    public int CheckIntervalMinutes { get; set; }

    public bool IsHealthy => ConsecutiveFailures < 3;

    public DateTime? NextScheduledUpdate =>
        LastSuccessfulUpdate?.AddHours(UpdateIntervalHours);
}