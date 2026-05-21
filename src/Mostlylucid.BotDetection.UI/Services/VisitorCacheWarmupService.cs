using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
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

    public VisitorCacheWarmupService(
        IDashboardEventStore eventStore,
        VisitorListCache cache,
        ILogger<VisitorCacheWarmupService> logger)
    {
        _eventStore = eventStore;
        _cache = cache;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

            var distinctBySignature = detections
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