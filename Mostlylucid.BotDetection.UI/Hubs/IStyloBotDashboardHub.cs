using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Hubs;

/// <summary>
///     SignalR hub contract for real-time bot detection dashboard updates.
///     Clients subscribe to receive live detection events and signature updates.
/// </summary>
public interface IStyloBotDashboardHub
{
    /// <summary>
    ///     Broadcast a new bot detection event to all connected dashboard clients.
    /// </summary>
    /// <param name="detection">The detection event to broadcast</param>
    Task BroadcastDetection(DashboardDetectionEvent detection);

    /// <summary>
    ///     Broadcast a new signature observation to the scrolling signatures feed.
    /// </summary>
    /// <param name="signature">The signature to broadcast</param>
    Task BroadcastSignature(DashboardSignatureEvent signature);

    /// <summary>
    ///     Broadcast updated summary statistics.
    /// </summary>
    /// <param name="summary">Current summary statistics</param>
    Task BroadcastSummary(DashboardSummary summary);
}