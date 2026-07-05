using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Periodically updates the bot detection lists. Fail-safe by design: every
///     failure is logged but never crashes the application.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> that
///         initialised the database on the first iteration and then looped on
///         <c>Task.Delay(CalculateNextCheckDelay())</c> with exponential
///         backoff. Now subscribes to <see cref="TickCadence.Tick1h"/> and
///         gates the check on "last-attempt older than the backoff-adjusted
///         check interval"; the database is initialised lazily on the first
///         tick so the boot path stays subscribe-only. The startup
///         <c>StartupDelaySeconds</c> grace window is preserved by tracking
///         the first-tick timestamp.
///     </para>
/// </summary>
public sealed class BotListUpdateService : IDisposable
{
    private readonly IBotListDatabase _database;
    private readonly ILogger<BotListUpdateService> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly BotDetectionOptions _options;
    private readonly ICompiledPatternCache? _patternCache;
    private readonly Mostlylucid.Ephemeral.TypedSignalSink<BotListUpdatedSignal>? _updateSignals;
    private readonly IDisposable? _subscription;
    private DateTime _firstTickUtc = DateTime.MinValue;
    private DateTime _lastAttemptUtc = DateTime.MinValue;
    private bool _databaseInitialised;
    private int _consecutiveFailures;
    private DateTime? _lastSuccessfulUpdate;
    private int _disposed;

    public BotListUpdateService(
        IBotListDatabase database,
        ILogger<BotListUpdateService> logger,
        IOptions<BotDetectionOptions> options,
        ICompiledPatternCache? patternCache = null,
        BotDetectionMetrics? metrics = null,
        IScheduleCoordinator? scheduleCoordinator = null,
        Mostlylucid.Ephemeral.TypedSignalSink<BotListUpdatedSignal>? updateSignals = null)
    {
        _database = database;
        _logger = logger;
        _options = options.Value;
        _patternCache = patternCache;
        _metrics = metrics;
        _updateSignals = updateSignals;

        // Optional so existing direct-construction tests keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1h,
                "BotListUpdateService",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Fires every Tick1h; honours the
    ///     legacy 60-minute check interval, 24-hour update interval, optional
    ///     startup-delay grace window, and exponential backoff on consecutive
    ///     failures. Public so tests can drive a single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;

        if (!_options.EnableBackgroundUpdates)
        {
            if (_firstTickUtc == DateTime.MinValue)
            {
                _logger.LogInformation(
                    "Bot list background updates are disabled. Lists will only be loaded once at startup");
                _firstTickUtc = now.UtcDateTime;
            }
            return;
        }

        if (_firstTickUtc == DateTime.MinValue)
        {
            _logger.LogInformation(
                "Bot list update service started. Schedule: {Schedule}",
                _options.UpdateSchedule?.Description ?? "default (daily at 2 AM UTC)");
            _firstTickUtc = now.UtcDateTime;
        }

        // Preserve the StartupDelaySeconds grace window: skip the first tick(s)
        // until the configured grace period has elapsed since the first tick.
        if (_options.StartupDelaySeconds > 0)
        {
            var grace = TimeSpan.FromSeconds(_options.StartupDelaySeconds);
            if (now.UtcDateTime - _firstTickUtc < grace) return;
        }

        // Honour the backoff-adjusted check interval since the last attempt.
        var checkInterval = CalculateNextCheckDelay();
        if (_lastAttemptUtc != DateTime.MinValue &&
            now.UtcDateTime - _lastAttemptUtc < checkInterval)
        {
            return; // Not yet due.
        }

        _lastAttemptUtc = now.UtcDateTime;

        // Initialize database on first eligible tick (was the first iteration
        // of the legacy ExecuteAsync loop).
        if (!_databaseInitialised)
        {
            await InitializeDatabaseSafeAsync(ct);
            _databaseInitialised = true;
        }

        try
        {
            await CheckAndUpdateListsAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown.
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogError(ex,
                "Unexpected error in bot list update tick (failure #{FailureCount}). " +
                "Service will continue running",
                _consecutiveFailures);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
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
            // Check if update is needed
            var lastUpdate = await _database.GetLastUpdateTimeAsync("bot_patterns", cancellationToken);
            var updateInterval = TimeSpan.FromHours(24); // Default: 24 hours (use UpdateSchedule cron for custom intervals)

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

    internal async Task PerformUpdateWithRetriesAsync(CancellationToken cancellationToken = default)
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

                var recoveredFrom = _consecutiveFailures;
                _lastSuccessfulUpdate = DateTime.UtcNow;
                _consecutiveFailures = 0;

                // Notify subscribers -- reactive replacement for the
                // previous "consumers poll IBotListDatabase" pattern.
                // Task-#65 reference implementation. Optional dep so
                // hosts that don't wire the sink still get the update
                // (the sink is a notification bus, not a data channel;
                // the database write above is the source of truth).
                _updateSignals?.Raise(
                    signal: BotListUpdatedSignal.Key.Name,
                    payload: new BotListUpdatedSignal
                    {
                        Timestamp = DateTimeOffset.UtcNow,
                        RecoveredFromFailures = recoveredFrom,
                    });

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
        const int checkIntervalMinutes = 60; // Default: 60 minutes (use UpdateSchedule cron for custom intervals)
        const int updateIntervalHours = 24; // Default: 24 hours
        var baseDelay = TimeSpan.FromMinutes(checkIntervalMinutes);

        // If we've had consecutive failures, use exponential backoff
        if (_consecutiveFailures > 0)
        {
            var backoffMinutes = Math.Min(
                checkIntervalMinutes * Math.Pow(1.5, _consecutiveFailures),
                updateIntervalHours * 60 // Cap at update interval
            );

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
        return new BotListUpdateStatus
        {
            IsEnabled = _options.EnableBackgroundUpdates,
            LastSuccessfulUpdate = _lastSuccessfulUpdate,
            ConsecutiveFailures = _consecutiveFailures,
            UpdateIntervalHours = 24,
            CheckIntervalMinutes = 60
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