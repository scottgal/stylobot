using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Broadcasts background LLM cluster description updates via SignalR.
///     Dashboard clients receive live updates as cluster descriptions are generated.
/// </summary>
public class ClusterDescriptionSignalRCallback : IClusterDescriptionCallback
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly ILogger<ClusterDescriptionSignalRCallback> _logger;

    public ClusterDescriptionSignalRCallback(
        ILogger<ClusterDescriptionSignalRCallback> logger,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
    }

    public async Task OnClusterDescriptionUpdatedAsync(
        string clusterId, string label, string description, CancellationToken ct = default)
    {
        // Beacon-only: signal that clusters changed, clients re-fetch via HTMX
        await _hubContext.Clients.All.BroadcastInvalidation("clusters");
        _logger.LogDebug("Broadcast cluster description invalidation for {ClusterId}: '{Label}'",
            clusterId[..Math.Min(16, clusterId.Length)], label);
    }

    public async Task OnClustersRefreshedAsync(
        IReadOnlyList<BotCluster> clusters, CancellationToken ct = default)
    {
        // Beacon-only: signal that clusters changed, clients fetch fresh HTML via HTMX
        await _hubContext.Clients.All.BroadcastInvalidation("clusters");
        _logger.LogDebug("Broadcast cluster invalidation for {Count} clusters", clusters.Count);
    }
}
