namespace Mostlylucid.BotDetection.Console.Services;

/// <summary>
///     Heartbeat service to detect silent failures - logs every 5 minutes
/// </summary>
public class HeartbeatService : BackgroundService
{
    private readonly ILogger<HeartbeatService> _logger;
    private int _heartbeatCount;

    public HeartbeatService(ILogger<HeartbeatService> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Heartbeat service started - will log every 5 minutes to detect silent failures");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);

                _heartbeatCount++;
                _logger.LogInformation("💓 HEARTBEAT #{Count} - Gateway still running (Uptime: {Uptime} minutes)",
                    _heartbeatCount,
                    _heartbeatCount * 5);

                // Log memory usage to detect memory leaks
                var memoryMB = GC.GetTotalMemory(false) / 1024 / 1024;
                _logger.LogInformation("   Memory: {MemoryMB} MB, GC Gen0: {Gen0}, Gen1: {Gen1}, Gen2: {Gen2}",
                    memoryMB,
                    GC.CollectionCount(0),
                    GC.CollectionCount(1),
                    GC.CollectionCount(2));
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Heartbeat service stopped (shutdown requested)");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Heartbeat service failed - THIS SHOULD NEVER HAPPEN");
            throw; // Re-throw to crash the app and surface the issue
        }
    }
}