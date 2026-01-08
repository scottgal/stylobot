using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Coordinator ATOM for managing parallel bot list updates from external sources.
///     ARCHITECTURE:
///     - Uses atom pattern for coordinated lifecycle management
///     - Fetches ALL data sources in PARALLEL at startup
///     - Configuration-ready for cron-based scheduling (UpdateSchedule section)
///     - Fail-safe: individual source failures don't block other sources
///     - Non-blocking: updates happen in background, never block requests
///     DATA SOURCES (configurable via BotDetectionOptions.DataSources):
///     1. Bot patterns: isbot, Matomo, crawler-user-agents
///     2. Datacenter IPs: AWS, GCP, Azure, Cloudflare
///     3. Security tools: digininja/scanner_user_agents, OWASP CoreRuleSet
///     LIFECYCLE:
///     1. Startup: Parallel fetch all enabled sources (non-blocking after startup delay)
///     2. Schedule: Configured via UpdateSchedule.Cron (integration with scheduler external to this class)
///     3. Updates: Parallel fetch → update database → refresh caches
///     4. Disposal: Cancel in-progress work, clean up resources
///     SCHEDULER INTEGRATION (FUTURE):
///     External scheduler (like Hangfire, Quartz, or custom) should call UpdateAllListsParallelAsync()
///     based on the cron expression in BotDetectionOptions.UpdateSchedule.Cron
/// </summary>
public sealed class ListUpdateCoordinatorAtom : IAsyncDisposable
{
    private readonly IBotListDatabase _database;
    private readonly CancellationTokenSource _disposalCts = new();
    private readonly IBotListFetcher _fetcher;
    private readonly ILogger<ListUpdateCoordinatorAtom> _logger;
    private readonly BotDetectionOptions _options;
    private readonly ICompiledPatternCache? _patternCache;
    private int _consecutiveFailures;

    // Update statistics
    private DateTime? _lastSuccessfulUpdate;
    private int _totalIpRangesFetched;
    private int _totalPatternsFetched;
    private int _totalSecurityToolsFetched;

    public ListUpdateCoordinatorAtom(
        IBotListFetcher fetcher,
        IBotListDatabase database,
        ILogger<ListUpdateCoordinatorAtom> logger,
        BotDetectionOptions options,
        ICompiledPatternCache? patternCache = null)
    {
        _fetcher = fetcher;
        _database = database;
        _logger = logger;
        _options = options;
        _patternCache = patternCache;
    }

    public async ValueTask DisposeAsync()
    {
        _logger.LogDebug("Disposing list update coordinator");

        // Cancel any in-progress work
        _disposalCts.Cancel();

        // Cleanup resources
        _disposalCts.Dispose();

        await Task.CompletedTask;
    }

    /// <summary>
    ///     Starts the coordinator: parallel fetch at startup.
    ///     For scheduled updates, external scheduler should call UpdateAllListsParallelAsync()
    ///     based on BotDetectionOptions.UpdateSchedule.Cron configuration.
    /// </summary>
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var schedule = _options.UpdateSchedule;
        if (schedule != null)
        {
            _logger.LogInformation(
                "List update coordinator starting - Parallel fetch enabled, Cron schedule configured: {Cron} ({Timezone})",
                schedule.Cron, schedule.Timezone);
            _logger.LogInformation(
                "External scheduler should call UpdateAllListsParallelAsync() based on cron: {Cron}",
                schedule.Cron);
        }
        else
        {
            _logger.LogInformation(
                "List update coordinator starting - Parallel fetch enabled, No schedule configured");
        }

        // Delay startup to avoid slowing down application startup
        if (_options.StartupDelaySeconds > 0)
        {
            _logger.LogDebug("Delaying list update by {Seconds} seconds to avoid blocking startup",
                _options.StartupDelaySeconds);

            await Task.Delay(TimeSpan.FromSeconds(_options.StartupDelaySeconds), cancellationToken);
        }

        // Initial parallel fetch at startup (if configured)
        if (schedule?.RunOnStartup ?? true)
            await UpdateAllListsParallelAsync(cancellationToken);
        else
            _logger.LogInformation("RunOnStartup=false - skipping initial update, waiting for scheduler");
    }

    /// <summary>
    ///     Fetches and updates ALL data sources in parallel.
    ///     Fail-safe: individual source failures logged but don't block others.
    ///     PUBLIC for external scheduler integration - call this method from your cron scheduler
    ///     based on the configured BotDetectionOptions.UpdateSchedule.Cron expression.
    /// </summary>
    public async Task UpdateAllListsParallelAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Starting parallel fetch from all enabled data sources");
        var startTime = DateTime.UtcNow;

        // Create tasks for all data source categories
        var tasks = new List<Task>
        {
            UpdateBotPatternsAsync(cancellationToken),
            UpdateDatacenterIpRangesAsync(cancellationToken),
            UpdateSecurityToolPatternsAsync(cancellationToken)
        };

        // Wait for all tasks to complete (parallelized)
        try
        {
            await Task.WhenAll(tasks);

            var elapsed = DateTime.UtcNow - startTime;
            _lastSuccessfulUpdate = DateTime.UtcNow;
            _consecutiveFailures = 0;

            _logger.LogInformation(
                "Parallel fetch completed in {ElapsedMs}ms - Patterns: {Patterns}, IPs: {IpRanges}, Security: {Security}",
                elapsed.TotalMilliseconds, _totalPatternsFetched, _totalIpRangesFetched, _totalSecurityToolsFetched);

            // TODO: Emit signal on successful update when SignalSink is implemented
            // Signal: _options.UpdateSchedule?.Signal (e.g., "botlist.update")
        }
        catch (Exception ex)
        {
            _consecutiveFailures++;
            _logger.LogError(ex,
                "Parallel fetch failed (failure #{FailureCount}) - Some sources may have succeeded",
                _consecutiveFailures);
        }
    }

    /// <summary>
    ///     Updates bot patterns from all enabled sources (isbot, Matomo, crawler-user-agents).
    ///     Fetcher handles parallelization internally and returns merged results.
    /// </summary>
    private async Task UpdateBotPatternsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Fetching bot patterns from enabled sources");

            // Fetch patterns (BotListFetcher handles parallel fetching internally)
            var patterns = await _fetcher.GetBotPatternsAsync(cancellationToken);
            var matomoPatterns = await _fetcher.GetMatomoBotPatternsAsync(cancellationToken);

            _totalPatternsFetched = patterns.Count + matomoPatterns.Count;

            // Update database (this also updates list_updates timestamp)
            await _database.UpdateListsAsync(cancellationToken);

            // Update compiled pattern cache if available
            if (_patternCache != null)
            {
                var dbPatterns = await _database.GetBotPatternsAsync(cancellationToken);
                _patternCache.UpdateDownloadedPatterns(dbPatterns);

                _logger.LogDebug("Updated pattern cache with {Count} compiled patterns", dbPatterns.Count);
            }

            _logger.LogInformation("Updated bot patterns: {Count} total patterns", _totalPatternsFetched);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update bot patterns - will retry later");
        }
    }

    /// <summary>
    ///     Updates datacenter IP ranges from all enabled sources (AWS, GCP, Azure, Cloudflare).
    ///     Fetcher handles parallelization internally and returns merged results.
    /// </summary>
    private async Task UpdateDatacenterIpRangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Fetching datacenter IP ranges from enabled sources");

            // Fetch IP ranges (BotListFetcher handles parallel fetching internally)
            var ipRanges = await _fetcher.GetDatacenterIpRangesAsync(cancellationToken);

            _totalIpRangesFetched = ipRanges.Count;

            // Update compiled CIDR cache if available
            if (_patternCache != null)
            {
                var dbRanges = await _database.GetDatacenterIpRangesAsync(cancellationToken);
                _patternCache.UpdateDownloadedCidrRanges(dbRanges);

                _logger.LogDebug("Updated CIDR cache with {Count} parsed ranges", dbRanges.Count);
            }

            _logger.LogInformation("Updated datacenter IP ranges: {Count} total ranges", _totalIpRangesFetched);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update datacenter IP ranges - will retry later");
        }
    }

    /// <summary>
    ///     Updates security tool patterns from enabled sources (digininja, OWASP CoreRuleSet).
    ///     These are used by SecurityToolContributor for detecting penetration testing tools.
    /// </summary>
    private async Task UpdateSecurityToolPatternsAsync(CancellationToken cancellationToken)
    {
        try
        {
            _logger.LogDebug("Fetching security tool patterns from enabled sources");

            // Fetch security tool patterns (BotListFetcher handles parallel fetching internally)
            var securityTools = await _fetcher.GetSecurityToolPatternsAsync(cancellationToken);

            _totalSecurityToolsFetched = securityTools.Count;

            // Security tool patterns are cached in-memory by the SecurityToolContributor
            // No database storage needed (they're lightweight and change infrequently)

            _logger.LogInformation("Updated security tool patterns: {Count} total patterns",
                _totalSecurityToolsFetched);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update security tool patterns - will retry later");
        }
    }


    /// <summary>
    ///     Gets current status of the coordinator.
    /// </summary>
    public ListUpdateCoordinatorStatus GetStatus()
    {
        var schedule = _options.UpdateSchedule;

        return new ListUpdateCoordinatorStatus
        {
            LastSuccessfulUpdate = _lastSuccessfulUpdate,
            ConsecutiveFailures = _consecutiveFailures,
            TotalPatternsFetched = _totalPatternsFetched,
            TotalIpRangesFetched = _totalIpRangesFetched,
            TotalSecurityToolsFetched = _totalSecurityToolsFetched,
            ScheduleCron = schedule?.Cron,
            ScheduleTimezone = schedule?.Timezone,
            IsHealthy = _consecutiveFailures < 3
        };
    }
}

/// <summary>
///     Status information for the list update coordinator.
/// </summary>
public class ListUpdateCoordinatorStatus
{
    public DateTime? LastSuccessfulUpdate { get; set; }
    public int ConsecutiveFailures { get; set; }
    public int TotalPatternsFetched { get; set; }
    public int TotalIpRangesFetched { get; set; }
    public int TotalSecurityToolsFetched { get; set; }
    public string? ScheduleCron { get; set; }
    public string? ScheduleTimezone { get; set; }
    public bool IsHealthy { get; set; }
}