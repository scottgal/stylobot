using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Background service that periodically computes all dashboard aggregates
///     and broadcasts summary statistics to connected clients.
///     This is the SINGLE place where top bots, countries, and user agents are computed.
///     API endpoints read from <see cref="DashboardAggregateCache" /> - no inline computation.
/// </summary>
public class DashboardSummaryBroadcaster : BackgroundService
{
    /// <summary>
    ///     Row count cached for the default-view detections endpoint. Matches
    ///     the upstream <c>HandleDetections</c> per-call ceiling so callers
    ///     asking for any limit at or below this read straight from cache.
    /// </summary>
    private const int DefaultCachedDetectionLimit = 200;

    /// <summary>
    ///     Time-series window cached for the default-view endpoint --
    ///     trailing-24 h at 5-minute buckets covers the dashboard's
    ///     "Traffic over time" chart out of the box.
    /// </summary>
    private const int DefaultCachedTimeSeriesWindowHours = 24;
    private const int DefaultCachedTimeSeriesBucketMinutes = 5;

    /// <summary>
    ///     Top-bots ranking depth cached on each tick. Sized to cover
    ///     the dashboard's "All / Bots / Humans" Live Activity table
    ///     without a tail of cache misses on pagination.
    /// </summary>
    private const int DefaultCachedTopBotsLimit = 100;

    /// <summary>
    ///     Threat ranking depth cached on each tick (the dashboard's
    ///     threats card surfaces the top handful).
    /// </summary>
    private const int DefaultCachedThreatsLimit = 20;

    private readonly IDashboardEventStore _eventStore;
    private readonly DashboardAggregateCache _cache;
    private readonly SignatureAggregateCache _signatureCache;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly ILogger<DashboardSummaryBroadcaster> _logger;
    private readonly StyloBotDashboardOptions _options;
    private readonly IServiceProvider _serviceProvider;
    private readonly DashboardUserAgentAggregator _uaAggregator;
    private bool _seeded;

    public DashboardSummaryBroadcaster(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        DashboardAggregateCache cache,
        SignatureAggregateCache signatureCache,
        StyloBotDashboardOptions options,
        IServiceProvider serviceProvider,
        ILogger<DashboardSummaryBroadcaster> logger,
        DashboardUserAgentAggregator uaAggregator)
    {
        _hubContext = hubContext;
        _eventStore = eventStore;
        _cache = cache;
        _signatureCache = signatureCache;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _uaAggregator = uaAggregator;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Dashboard broadcaster started (interval: {Interval}s)",
            _options.SummaryBroadcastIntervalSeconds);

        // Wait briefly for database schema initialization to complete before querying.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken);
        }
        catch (OperationCanceledException) { return; }

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                // Seed SignatureAggregateCache from DB on first iteration.
                // Retry if sessions table isn't created yet (fresh install race condition).
                if (!_seeded)
                {
                    try
                    {
                        var seedBots = await _eventStore.GetTopBotsAsync(100);
                        _signatureCache.SeedFromTopBots(seedBots);
                        _seeded = true;
                        _logger.LogInformation("Seeded SignatureAggregateCache with {Count} entries from DB", seedBots.Count);
                    }
                    catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.Message.Contains("no such table"))
                    {
                        // Sessions table not yet created (fresh install) — retry next cycle
                        _logger.LogDebug("Sessions table not ready yet, will retry seed next cycle");
                    }
                    catch (Exception ex)
                    {
                        _seeded = true; // don't spam on non-transient errors
                        _logger.LogWarning(ex, "Failed to seed SignatureAggregateCache from DB");
                    }
                }

                // Idle-skip: if no one has consumed the /api/v1 surface for
                // longer than the configured window, the precompute would be
                // burning CPU and DB I/O for an empty cache. Park the tick
                // until the next request lands. The first endpoint hit after
                // a quiet period serves a slightly stale snapshot (the next
                // tick refreshes it within SummaryBroadcastIntervalSeconds);
                // dashboards that are open continuously stamp MarkHit every
                // few seconds via the running SSR + broadcaster polling and
                // never trip this path.
                if (_options.AggregateCacheIdleSkipSeconds > 0)
                {
                    var lastHit = _cache.LastHitAtUtc;
                    if (lastHit != DateTime.MinValue
                        && DateTime.UtcNow - lastHit
                            > TimeSpan.FromSeconds(_options.AggregateCacheIdleSkipSeconds))
                    {
                        await Task.Delay(
                            TimeSpan.FromSeconds(_options.SummaryBroadcastIntervalSeconds),
                            stoppingToken);
                        continue;
                    }
                }

                // Compute aggregates from DB in parallel. Every aggregate the
                // /api/v1 read surface exposes for the default-view dashboard
                // is precomputed here so the endpoint handlers can return
                // straight from the snapshot. Filtered / windowed queries
                // (custom since/until, non-default limits) still fall through
                // to the store, but the home dashboard's hot path no longer
                // pays per-request store latency.
                var summaryTask = _eventStore.GetSummaryAsync();
                var countriesTask = _eventStore.GetCountryStatsAsync(50);
                var endpointsTask = _eventStore.GetEndpointStatsAsync(50);
                var userAgentsTask = ComputeUserAgentsAsync();
                var detectionsTask = _eventStore.GetDetectionsAsync(new DashboardFilter
                {
                    Limit = DefaultCachedDetectionLimit
                }, stoppingToken);
                var timeSeriesStart = DateTime.UtcNow.AddHours(-DefaultCachedTimeSeriesWindowHours);
                var timeSeriesTask = _eventStore.GetTimeSeriesAsync(
                    timeSeriesStart, DateTime.UtcNow,
                    TimeSpan.FromMinutes(DefaultCachedTimeSeriesBucketMinutes));
                var topBotsTask = _eventStore.GetTopBotsAsync(DefaultCachedTopBotsLimit);
                var threatsTask = _eventStore.GetThreatsAsync(DefaultCachedThreatsLimit);

                await Task.WhenAll(
                    summaryTask, countriesTask, endpointsTask, userAgentsTask,
                    detectionsTask, timeSeriesTask, topBotsTask, threatsTask);

                // Update cache atomically
                _cache.Update(new DashboardAggregateCache.AggregateSnapshot
                {
                    Countries = await countriesTask,
                    Endpoints = await endpointsTask,
                    UserAgents = await userAgentsTask,
                    Summary = await summaryTask,
                    Detections = await detectionsTask,
                    TimeSeries = await timeSeriesTask,
                    TopBots = (await topBotsTask).ToList(),
                    Threats = (await threatsTask).ToList()
                });

                // Send lightweight invalidation signals through the constrainer
                // so all dashboard broadcasts share the same outbound 10s window
                // -- direct hub.BroadcastInvalidation calls bypassed the rate cap
                // and the client observed refresh gaps of ~3.6s under load even
                // though every individual caller "obeyed" its own schedule.
                SignalRBroadcastConstrainer.Queue(_hubContext, "summary",    _options.BroadcastMinIntervalMs);
                SignalRBroadcastConstrainer.Queue(_hubContext, "countries",  _options.BroadcastMinIntervalMs);
                SignalRBroadcastConstrainer.Queue(_hubContext, "endpoints",  _options.BroadcastMinIntervalMs);
                SignalRBroadcastConstrainer.Queue(_hubContext, "signature",  _options.BroadcastMinIntervalMs);
                SignalRBroadcastConstrainer.Queue(_hubContext, "useragents", _options.BroadcastMinIntervalMs);
                SignalRBroadcastConstrainer.Queue(_hubContext, "metrics",    _options.BroadcastMinIntervalMs);

                // Prune detections older than 7 days on each tick so storage stays bounded
                // without requiring a process restart.
                try
                {
                    var pruned = await _eventStore.PruneOldDetectionsAsync(
                        DateTime.UtcNow.AddDays(-7), stoppingToken);
                    if (pruned > 0)
                        _logger.LogDebug("Pruned {Count} old dashboard detections", pruned);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to prune old detections");
                }

                try
                {
                    var snapshotStore = _serviceProvider.GetService<IMetricSnapshotStore>();
                    if (snapshotStore != null)
                    {
                        var pruned = await snapshotStore.PruneOldSnapshotsAsync(
                            DateTime.UtcNow.AddDays(-7), stoppingToken);
                        if (pruned > 0)
                            _logger.LogDebug("Pruned {Count} old metric snapshots", pruned);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to prune old metric snapshots");
                }

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.SummaryBroadcastIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error computing dashboard aggregates");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

        _logger.LogInformation("Dashboard broadcaster stopped");
    }

    /// <summary>
    ///     Compute user agent aggregates from detections.
    ///     Delegates to <see cref="DashboardUserAgentAggregator" /> with no args so the
    ///     beacon-tick behavior (all traffic, last 500 detections) is unchanged.
    ///     The public bridge is retained so existing unit tests that call this method
    ///     directly on the broadcaster continue to compile without modification.
    /// </summary>
    internal Task<List<DashboardUserAgentSummary>> ComputeUserAgentsAsync()
        => _uaAggregator.ComputeAsync();
}