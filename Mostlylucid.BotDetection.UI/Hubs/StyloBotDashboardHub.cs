using Microsoft.AspNetCore.SignalR;

namespace Mostlylucid.BotDetection.UI.Hubs;

/// <summary>
///     SignalR hub for broadcasting real-time bot detection events to dashboard clients.
/// </summary>
public class StyloBotDashboardHub : Hub<IStyloBotDashboardHub>
{
    /// <summary>
    ///     Client connects and joins the dashboard group.
    /// </summary>
    public override async Task OnConnectedAsync()
    {
        await Groups.AddToGroupAsync(Context.ConnectionId, "Dashboard");
        await base.OnConnectedAsync();
    }

    /// <summary>
    ///     Client disconnects.
    /// </summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, "Dashboard");
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    ///     Client requests current summary statistics.
    /// </summary>
    public Task RequestSummary()
    {
        // Handled by DashboardSummaryBroadcaster
        return Task.CompletedTask;
    }
}