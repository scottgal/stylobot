using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Background service that generates LLM-based descriptions for bot clusters (GraphRAG-style).
///     Subscribes to BotClusterService.ClustersUpdated events and processes clusters asynchronously.
///     NEVER runs in the request pipeline - only fires after background clustering completes.
///     Pushes updates via IClusterDescriptionCallback (SignalR) so dashboard users see live updates.
///     Uses ILlmProvider (from plugin packages) if available; otherwise skips LLM descriptions.
/// </summary>
public class BotClusterDescriptionService : IDisposable
{
    private readonly ILogger<BotClusterDescriptionService> _logger;
    private readonly BotClusterService _clusterService;
    private readonly ClusterOptions _options;
    private readonly LlmDescriptionCoordinator _coordinator;
    private readonly IClusterDescriptionCallback? _callback;

    public BotClusterDescriptionService(
        ILogger<BotClusterDescriptionService> logger,
        BotClusterService clusterService,
        IOptions<BotDetectionOptions> options,
        LlmDescriptionCoordinator coordinator,
        IClusterDescriptionCallback? callback = null)
    {
        _logger = logger;
        _clusterService = clusterService;
        _options = options.Value.Cluster;
        _coordinator = coordinator;
        _callback = callback;

        if (_options.EnableLlmDescriptions || _callback != null)
        {
            _clusterService.ClustersUpdated += OnClustersUpdated;
            if (_options.EnableLlmDescriptions)
                _logger.LogInformation(
                    "BotClusterDescriptionService enabled (model={Model})",
                    _options.DescriptionModel);
            else
                _logger.LogInformation("BotClusterDescriptionService: LLM disabled, broadcasting cluster updates only");
        }
    }

    private void OnClustersUpdated(IReadOnlyList<BotCluster> clusters, IReadOnlyList<SignatureBehavior> behaviors)
    {
        // Broadcast cluster refresh via callback (non-LLM, immediate)
        if (_callback != null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _callback.OnClustersRefreshedAsync(clusters, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to broadcast cluster refresh");
                }
            });
        }

        // Enqueue clusters needing descriptions to the constrained coordinator
        _ = EnqueueClustersForDescriptionAsync(clusters, behaviors);
    }

    private async Task EnqueueClustersForDescriptionAsync(
        IReadOnlyList<BotCluster> clusters,
        IReadOnlyList<SignatureBehavior> behaviors)
    {
        if (!_options.EnableLlmDescriptions)
            return;

        var behaviorMap = behaviors.ToDictionary(b => b.Signature);

        foreach (var cluster in clusters.Where(c => string.IsNullOrEmpty(c.Description)))
        {
            try
            {
                var members = cluster.MemberSignatures
                    .Where(s => behaviorMap.ContainsKey(s))
                    .Select(s => behaviorMap[s])
                    .ToList();

                if (members.Count == 0) continue;

                await _coordinator.EnqueueClusterAsync(
                    cluster.ClusterId, cluster, members, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to enqueue cluster {ClusterId} for description",
                    cluster.ClusterId);
            }
        }
    }

    public void Dispose()
    {
        _clusterService.ClustersUpdated -= OnClustersUpdated;
    }
}

/// <summary>
///     Callback interface for live cluster description updates via SignalR.
/// </summary>
public interface IClusterDescriptionCallback
{
    Task OnClusterDescriptionUpdatedAsync(
        string clusterId, string label, string description, CancellationToken ct = default);

    Task OnClustersRefreshedAsync(
        IReadOnlyList<BotCluster> clusters, CancellationToken ct = default)
        => Task.CompletedTask;
}
