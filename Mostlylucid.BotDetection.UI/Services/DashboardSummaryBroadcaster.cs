using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Background service that periodically broadcasts summary statistics to dashboard clients.
/// </summary>
public class DashboardSummaryBroadcaster : BackgroundService
{
    private readonly IDashboardEventStore _eventStore;
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly ILogger<DashboardSummaryBroadcaster> _logger;
    private readonly StyloBotDashboardOptions _options;

    public DashboardSummaryBroadcaster(
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IDashboardEventStore eventStore,
        StyloBotDashboardOptions options,
        ILogger<DashboardSummaryBroadcaster> logger)
    {
        _hubContext = hubContext;
        _eventStore = eventStore;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "Dashboard summary broadcaster started (interval: {Interval}s)",
            _options.SummaryBroadcastIntervalSeconds);

        while (!stoppingToken.IsCancellationRequested)
            try
            {
                var summary = await _eventStore.GetSummaryAsync();
                await _hubContext.Clients.All.BroadcastSummary(summary);

                await Task.Delay(
                    TimeSpan.FromSeconds(_options.SummaryBroadcastIntervalSeconds),
                    stoppingToken);
            }
            catch (OperationCanceledException)
            {
                // Expected during shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error broadcasting summary");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }

        _logger.LogInformation("Dashboard summary broadcaster stopped");
    }
}