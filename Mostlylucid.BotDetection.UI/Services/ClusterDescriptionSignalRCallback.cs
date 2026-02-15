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
        await _hubContext.Clients.All.BroadcastClusterDescriptionUpdate(clusterId, label, description);
        _logger.LogDebug("Broadcast cluster description for {ClusterId}: '{Label}' - {Description}",
            clusterId[..Math.Min(16, clusterId.Length)],
            label,
            description.Length > 80 ? description[..80] + "..." : description);
    }

    public async Task OnClustersRefreshedAsync(
        IReadOnlyList<BotCluster> clusters, CancellationToken ct = default)
    {
        var events = clusters.Select(c => new DashboardClusterEvent
        {
            ClusterId = c.ClusterId,
            Label = c.Label ?? "Unnamed Cluster",
            Description = c.Description ?? "",
            Type = c.Type.ToString(),
            MemberCount = c.MemberCount,
            AvgBotProb = c.AverageBotProbability,
            Country = c.DominantCountry,
            AverageSimilarity = c.AverageSimilarity,
            TemporalDensity = c.TemporalDensity
        }).ToList();

        await _hubContext.Clients.All.BroadcastClusters(events);
        _logger.LogDebug("Broadcast {Count} clusters to dashboard", events.Count);
    }
}
