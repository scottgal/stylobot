using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Background service that rehydrates <see cref="SignatureAggregateCache"/> from the
///     persisted event store on startup.
///     <para>
///     Without this, every restart wiped the cache that feeds the "Top Bots" / "Live
///     Activity" / "live-visitors" widgets, so a fleet rolling under deploy would
///     show empty bot lists until fresh traffic arrived even though the underlying
///     PostgreSQL had years of history. The cache is and remains a read-through
///     hot cache -- this just seeds it from the source of truth on boot so the
///     window of "zero rows after restart" closes.
///     </para>
///     <para>
///     Mirrors <see cref="VisitorCacheWarmupService"/>: same 2s delay for the DB
///     connection pool, same try/catch fallback to "populate from live traffic"
///     if persistence is unreachable.
///     </para>
/// </summary>
public sealed class SignatureAggregateCacheWarmupService : BackgroundService
{
    private readonly IDashboardEventStore _eventStore;
    private readonly SignatureAggregateCache _cache;
    private readonly StyloBotDashboardOptions _options;
    private readonly ILogger<SignatureAggregateCacheWarmupService> _logger;

    public SignatureAggregateCacheWarmupService(
        IDashboardEventStore eventStore,
        SignatureAggregateCache cache,
        IOptions<StyloBotDashboardOptions> options,
        ILogger<SignatureAggregateCacheWarmupService> logger)
    {
        _eventStore = eventStore;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            await WarmAsync(stoppingToken, isFirstWarm: true);

            // Periodic re-warm only when SignatureAggregateRefreshIntervalSeconds > 0.
            // Remote-mode dashboard hosts opt into this because they don't run
            // DetectionBroadcastMiddleware, so without a periodic re-pull from the event
            // store (which is RemoteDashboardEventStore -> gateway REST in remote mode)
            // their cache freezes at startup-warm values and Live Activity / Top Bots
            // never reflect new gateway detections. Gateway-mode hosts leave it at 0
            // because UpdateFromDetection keeps the cache live on every request.
            var intervalSec = _options.SignatureAggregateRefreshIntervalSeconds;
            if (intervalSec <= 0) return;

            var interval = TimeSpan.FromSeconds(intervalSec);
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(interval, stoppingToken);
                    await WarmAsync(stoppingToken, isFirstWarm: false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Periodic signature aggregate cache refresh failed -- will retry on next tick");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task WarmAsync(CancellationToken stoppingToken, bool isFirstWarm)
    {
        try
        {
            // MaxEntries is the cap; pull at most that many. 24h window matches the
            // VisitorListCache warmup so both caches agree on "what was happening
            // recently" after a restart.
            var topBots = await _eventStore.GetTopBotsAsync(
                count: _cache.MaxEntries,
                startTime: DateTime.UtcNow.AddHours(-24),
                endTime: DateTime.UtcNow);

            if (topBots.Count > 0)
            {
                _cache.SeedFromTopBots(topBots);
                if (isFirstWarm)
                {
                    _logger.LogInformation(
                        "Warmed signature aggregate cache with {Count} signatures from last 24h",
                        topBots.Count);
                }
                else
                {
                    _logger.LogDebug(
                        "Refreshed signature aggregate cache with {Count} signatures from event store",
                        topBots.Count);
                }

                // Pre-warm the per-signature hit ring buffers from the last hour of stored
                // detections so the Live Activity sparkline column shows real trend on
                // first paint after restart. Subsequent periodic refreshes also reseed
                // them so sparklines on bursty bots keep advancing in remote mode.
                try
                {
                    var recent = await _eventStore.GetDetectionsAsync(new DashboardFilter
                    {
                        StartTime = DateTime.UtcNow.AddMinutes(-60),
                        Limit = 50_000,
                    }, stoppingToken);

                    if (recent.Count > 0)
                    {
                        recent.Reverse();
                        _cache.SeedHitTrendsFromDetections(recent);
                        if (isFirstWarm)
                        {
                            _logger.LogInformation(
                                "Pre-warmed sparkline ring buffers from {Count} recent detections",
                                recent.Count);
                        }
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Failed to (re)warm sparkline ring buffers -- they will build up from live traffic over the next hour");
                }
            }
            else if (isFirstWarm)
            {
                _logger.LogDebug("No recent top bots found to warm signature aggregate cache");
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            // Same fail-soft model as VisitorCacheWarmupService -- cache starts
            // empty (or stays at last-known state), live traffic fills it on a
            // gateway host. No detection-path impact.
            _logger.LogWarning(ex,
                "Failed to warm signature aggregate cache from event store -- will populate from live traffic / next tick");
        }
    }
}
