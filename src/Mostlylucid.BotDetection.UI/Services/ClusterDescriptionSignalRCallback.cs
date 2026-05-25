using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Hubs;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Broadcasts background LLM cluster description updates via SignalR.
///     Dashboard clients receive live updates as cluster descriptions are generated.
///     Routed through <see cref="SignalRBroadcastConstrainer"/> so a burst of
///     cluster updates -- the LLM can finish ten descriptions in quick succession
///     after a warm start -- still emits at most one beacon per
///     <c>BroadcastMinIntervalMs</c>.
/// </summary>
public class ClusterDescriptionSignalRCallback : IClusterDescriptionCallback
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hubContext;
    private readonly ILogger<ClusterDescriptionSignalRCallback> _logger;
    private readonly StyloBotDashboardOptions _options;

    public ClusterDescriptionSignalRCallback(
        ILogger<ClusterDescriptionSignalRCallback> logger,
        IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hubContext,
        IOptions<StyloBotDashboardOptions> options)
    {
        _logger = logger;
        _hubContext = hubContext;
        _options = options.Value;
    }

    public Task OnClusterDescriptionUpdatedAsync(
        string clusterId, string label, string description, CancellationToken ct = default)
    {
        SignalRBroadcastConstrainer.Queue(_hubContext, "clusters", _options.BroadcastMinIntervalMs);
        _logger.LogDebug("Queued cluster description invalidation for {ClusterId}: '{Label}'",
            clusterId[..Math.Min(16, clusterId.Length)], label);
        return Task.CompletedTask;
    }

    public Task OnClustersRefreshedAsync(
        IReadOnlyList<BotCluster> clusters, CancellationToken ct = default)
    {
        SignalRBroadcastConstrainer.Queue(_hubContext, "clusters", _options.BroadcastMinIntervalMs);
        _logger.LogDebug("Queued cluster invalidation for {Count} clusters", clusters.Count);
        return Task.CompletedTask;
    }
}
