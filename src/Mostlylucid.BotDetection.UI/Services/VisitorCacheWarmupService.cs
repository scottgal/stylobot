using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Background service that warms the VisitorListCache from the persisted event store on startup.
///     Without this, the "Top Bots" and visitor list are empty until new traffic arrives.
/// </summary>
public class VisitorCacheWarmupService : BackgroundService
{
    private readonly IDashboardEventStore _eventStore;
    private readonly VisitorListCache _cache;
    private readonly ILogger<VisitorCacheWarmupService> _logger;
    private readonly bool _isRemoteMode;

    public VisitorCacheWarmupService(
        IDashboardEventStore eventStore,
        VisitorListCache cache,
        ILogger<VisitorCacheWarmupService> logger,
        DashboardSourceOptions? sourceOptions = null)
    {
        _eventStore = eventStore;
        _cache = cache;
        _logger = logger;
        // Same remote-mode skip pattern as SignatureAggregateCacheWarmupService:
        // dashboard state lives on the gateway, this host reads via REST. Local
        // warmup would freeze at startup-warm and drift from the gateway truth.
        _isRemoteMode = sourceOptions?.Pull?.Type == DashboardSourceType.Rest;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_isRemoteMode)
        {
            _logger.LogInformation(
                "VisitorCacheWarmup: skipped (remote-mode host -- gateway owns the cache, dashboard reads via REST)");
            return;
        }

        try
        {
            // Small delay to let the DB connection pool initialize
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);

            // Pull a wide window of detection events and keep only the most recent per
            // signature before upserting. Previously the warmup pulled the last 200 raw
            // events and replayed them; when one chatty source (a load tester, an LLM
            // poll, a dashboard auto-refresh storm) produced > 200 of those events the
            // cache rehydrated with a single signature, blanking out every other bot
            // identity until live traffic eventually re-upserted them. Distinct-by-sig
            // up front gets one entry per unique fingerprint with deterministic priority
            // for the most recent event in that group's window.
            var filter = new DashboardFilter
            {
                Limit = 5000,
                StartTime = DateTime.UtcNow.AddHours(-24)
            };

            var detections = await _eventStore.GetDetectionsAsync(filter, stoppingToken);
            if (detections.Count == 0)
            {
                _logger.LogDebug("No recent detections found to warm visitor cache");
                return;
            }

            // Many persisted detection events carry a null/empty PrimarySignature --
            // static-asset hits, low-confidence early-pipeline drops, partial events
            // written before the signature engine assigned one. Without this filter
            // the GroupBy below collapses every nulled row into a single anonymous
            // group, which previously left the cache with one "(unknown)" visitor
            // covering 5000 events instead of one row per actual fingerprint.
            var distinctBySignature = detections
                .Where(d => !string.IsNullOrEmpty(d.PrimarySignature))
                .GroupBy(d => d.PrimarySignature)
                .Select(g => g.OrderByDescending(d => d.Timestamp).First())
                .ToList();

            _cache.WarmFrom(distinctBySignature);
            _logger.LogInformation(
                "Warmed visitor cache with {Distinct} distinct signatures from {Events} events in last 24h",
                distinctBySignature.Count, detections.Count);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to warm visitor cache from event store - will populate from live traffic");
        }
    }
}