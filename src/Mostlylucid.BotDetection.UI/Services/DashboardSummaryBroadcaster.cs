using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Periodically computes all dashboard aggregates and broadcasts summary
///     statistics to connected clients. The SINGLE place where top bots,
///     countries, and user agents are computed. API endpoints read from
///     <see cref="DashboardAggregateCache" /> -- no inline computation.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(SummaryBroadcastIntervalSeconds)</c> loop; now
///         subscribes to <see cref="TickCadence.Tick10s"/> on
///         <see cref="IScheduleCoordinator"/> and gates the compute pass on
///         "last-broadcast older than the configured interval". The seed,
///         prune, and idle-skip control paths all migrate to the tick handler
///         unchanged. Database initialisation now happens lazily on the first
///         eligible tick so the boot path stays subscribe-only.
///         See <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed class DashboardSummaryBroadcaster : IDisposable
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
    private readonly IDisposable? _subscription;
    private bool _seeded;
    private bool _firstTickAnnounced;
    private DateTime _lastComputeUtc = DateTime.MinValue;
    private int _disposed;

    /// <summary>
    ///     Retention pruning is hourly, not per-tick: a 7-day-cutoff DELETE every
    ///     5s churns the table and contends with the detection write path for no
    ///     benefit. Tracks when the last prune ran.
    /// </summary>
    private DateTime _lastPruneUtc = DateTime.MinValue;
    private static readonly TimeSpan PruneInterval = TimeSpan.FromHours(1);

    private readonly bool _isRemoteMode;

    public DashboardSummaryBroadcaster(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        DashboardAggregateCache cache,
        SignatureAggregateCache signatureCache,
        StyloBotDashboardOptions options,
        IServiceProvider serviceProvider,
        ILogger<DashboardSummaryBroadcaster> logger,
        DashboardUserAgentAggregator uaAggregator,
        DashboardSourceOptions? sourceOptions = null,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _hubContext = hubContext;
        _eventStore = eventStore;
        _cache = cache;
        _signatureCache = signatureCache;
        _options = options;
        _serviceProvider = serviceProvider;
        _logger = logger;
        _uaAggregator = uaAggregator;
        // Remote-mode dashboards consume the gateway's already-broadcast aggregates
        // over REST / SignalR; running the broadcaster here would push stale, locally-
        // computed snapshots onto the same hub and fight with the gateway's truth.
        // Gateway owns aggregation + broadcast; this host just renders.
        _isRemoteMode = sourceOptions?.Pull?.Type == DashboardSourceType.Rest;

        // Optional so existing direct-construction tests keep working.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick10s,
                "DashboardSummaryBroadcaster",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Fires every Tick10s; gates the
    ///     compute + broadcast pass on the configured
    ///     <see cref="StyloBotDashboardOptions.SummaryBroadcastIntervalSeconds"/>
    ///     since the last successful pass, honours remote-mode (which delegates
    ///     to the gateway) and the configured idle-skip window. Public so tests
    ///     can drive a single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (_isRemoteMode)
        {
            if (!_firstTickAnnounced)
            {
                _logger.LogInformation(
                    "Dashboard broadcaster skipped (remote-mode host -- gateway owns aggregation + SignalR)");
                _firstTickAnnounced = true;
            }
            return;
        }

        if (!_firstTickAnnounced)
        {
            _logger.LogInformation(
                "Dashboard broadcaster started (interval: {Interval}s)",
                _options.SummaryBroadcastIntervalSeconds);
            _firstTickAnnounced = true;
            // Preserve the legacy "brief delay before first compute" behaviour by
            // waiting one tick before doing real work; the original loop used a
            // 3-second sleep to let the database schema finish initialising.
            return;
        }

        var nowUtc = now.UtcDateTime;
        var interval = TimeSpan.FromSeconds(_options.SummaryBroadcastIntervalSeconds);
        if (_lastComputeUtc != DateTime.MinValue && nowUtc - _lastComputeUtc < interval)
        {
            return; // Not yet due.
        }

        try
        {
            await RunOnceAsync(nowUtc, ct);
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error computing dashboard aggregates");
        }
    }

    private async Task RunOnceAsync(DateTime nowUtc, CancellationToken ct)
    {
        _lastComputeUtc = nowUtc;

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

        // Bound storage on its own cadence (hourly), BEFORE the idle-skip
        // below -- detections accumulate from traffic whether or not anyone
        // is viewing the dashboard, so pruning must not depend on viewers.
        if (nowUtc - _lastPruneUtc >= PruneInterval)
        {
            _lastPruneUtc = nowUtc;
            try
            {
                var pruned = await _eventStore.PruneOldDetectionsAsync(
                    nowUtc.AddDays(-7), ct);
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
                        nowUtc.AddDays(-7), ct);
                    if (pruned > 0)
                        _logger.LogDebug("Pruned {Count} old metric snapshots", pruned);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to prune old metric snapshots");
            }
        }

        // Idle-skip: if nobody has consumed the dashboard surface for
        // longer than the configured window, the precompute would be
        // burning CPU and DB I/O (a full-table summary scan + 7 more
        // aggregates) for a snapshot no one reads. Park the tick until
        // the next request lands. "Consumed" is stamped by MarkHit from
        // the SSR page render, the OOB refresh endpoint, and the /api/v1
        // read endpoints (see DashboardAggregateCache.MarkHit). A server
        // that has NEVER been viewed (LastHitAtUtc == MinValue) is idle
        // by definition and parks too -- otherwise an unviewed gateway
        // would run every aggregate every tick forever.
        if (_options.AggregateCacheIdleSkipSeconds > 0)
        {
            var lastHit = _cache.LastHitAtUtc;
            var idle = lastHit == DateTime.MinValue
                || nowUtc - lastHit
                    > TimeSpan.FromSeconds(_options.AggregateCacheIdleSkipSeconds);
            if (idle) return;
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
        }, ct);
        var timeSeriesStart = nowUtc.AddHours(-DefaultCachedTimeSeriesWindowHours);
        var timeSeriesTask = _eventStore.GetTimeSeriesAsync(
            timeSeriesStart, nowUtc,
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
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
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