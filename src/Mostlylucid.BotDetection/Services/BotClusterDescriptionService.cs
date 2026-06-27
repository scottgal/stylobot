using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services.Llm;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Listens for <see cref="BotClusterService.ClustersUpdated"/> waves and fans
///     them out two ways: (1) immediate <see cref="IClusterDescriptionCallback"/>
///     broadcast for SignalR so dashboard users see cluster refreshes live, and
///     (2) <see cref="NeedsDescriptionClusterPicker.TrackClusters"/> push so the
///     ephemeral LLM-naming coordinator can pull the next batch on its tick.
///     NEVER runs in the request pipeline -- only fires after background
///     clustering completes.
///     <para>
///         As of EC6c the legacy <c>LlmDescriptionCoordinator</c> queue is gone;
///         the cluster-naming pipeline is now picker-driven against
///         <c>ScheduleCoordinator</c> Tick5m.
///     </para>
/// </summary>
public class BotClusterDescriptionService : IDisposable
{
    private readonly ILogger<BotClusterDescriptionService> _logger;
    private readonly BotClusterService _clusterService;
    private readonly ClusterOptions _options;
    private readonly NeedsDescriptionClusterPicker _clusterPicker;
    private readonly IClusterDescriptionCallback? _callback;

    public BotClusterDescriptionService(
        ILogger<BotClusterDescriptionService> logger,
        BotClusterService clusterService,
        IOptions<BotDetectionOptions> options,
        NeedsDescriptionClusterPicker clusterPicker,
        IClusterDescriptionCallback? callback = null)
    {
        _logger = logger;
        _clusterService = clusterService;
        _options = options.Value.Cluster;
        _clusterPicker = clusterPicker;
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

        // Push clusters that still need descriptions to the picker; the
        // EphemeralLlmCoordinator<ClusterPickItem,ClusterNamingResult> walks the
        // tracker on its next Tick5m. The picker's TrackClusters filter is the
        // same string.IsNullOrEmpty(cluster.Description) gate the legacy
        // EnqueueClustersForDescriptionAsync used.
        if (_options.EnableLlmDescriptions)
        {
            try
            {
                _clusterPicker.TrackClusters(clusters, behaviors);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to track clusters for LLM-naming picker");
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
