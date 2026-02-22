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

    /// <summary>
    ///     Broadcast an LLM-generated description update for a detection event.
    ///     Sent asynchronously after the detection was already broadcast.
    /// </summary>
    /// <param name="requestId">The request ID of the detection to update</param>
    /// <param name="description">The plain-english description</param>
    Task BroadcastDescriptionUpdate(string requestId, string description);

    /// <summary>
    ///     Broadcast an LLM-generated signature description (name + description) for a signature.
    ///     Sent when SignatureDescriptionService generates a name after reaching the request threshold.
    /// </summary>
    Task BroadcastSignatureDescriptionUpdate(string signature, string name, string description);

    /// <summary>
    ///     Broadcast an LLM-generated cluster description update.
    ///     Sent when background BotClusterDescriptionService generates a description.
    /// </summary>
    Task BroadcastClusterDescriptionUpdate(string clusterId, string label, string description);

    /// <summary>
    ///     Broadcast the full cluster list when clusters are refreshed.
    ///     Sent immediately when background clustering completes (before LLM descriptions).
    /// </summary>
    Task BroadcastClusters(List<DashboardClusterEvent> clusters);

    /// <summary>
    ///     Broadcast updated country statistics to connected clients.
    /// </summary>
    Task BroadcastCountries(List<DashboardCountryStats> countries);

    /// <summary>
    ///     Broadcast a score change narrative for a signature.
    /// </summary>
    Task BroadcastScoreNarrative(string signature, string narrative);

    /// <summary>
    ///     Broadcast the updated top bots list to connected clients.
    ///     Sent periodically so the dashboard reflects classification changes (bot→human flips).
    /// </summary>
    Task BroadcastTopBots(List<DashboardTopBotEntry> topBots);
}