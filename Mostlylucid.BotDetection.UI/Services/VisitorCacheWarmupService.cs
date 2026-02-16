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

            var filter = new DashboardFilter
            {
                Limit = 200,
                StartTime = DateTime.UtcNow.AddHours(-24)
            };

            var detections = await _eventStore.GetDetectionsAsync(filter);

            if (detections.Count > 0)
            {
                _cache.WarmFrom(detections);
                _logger.LogInformation("Warmed visitor cache with {Count} detections from last 24h", detections.Count);
            }
            else
            {
                _logger.LogDebug("No recent detections found to warm visitor cache");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to warm visitor cache from event store — will populate from live traffic");
        }
    }
}
