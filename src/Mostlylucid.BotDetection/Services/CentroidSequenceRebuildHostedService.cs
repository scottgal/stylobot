using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Wires BotClusterService.ClustersUpdated → CentroidSequenceStore.RebuildAsync
///     and initializes the SQLite table on startup.
/// </summary>
internal sealed class CentroidSequenceRebuildHostedService : IHostedService
{
    private readonly BotClusterService? _clusterService;
    private readonly CentroidSequenceStore _centroidStore;
    private readonly ILogger<CentroidSequenceRebuildHostedService> _logger;

    public CentroidSequenceRebuildHostedService(
        CentroidSequenceStore centroidStore,
        ILogger<CentroidSequenceRebuildHostedService> logger,
        BotClusterService? clusterService = null)
    {
        _centroidStore = centroidStore;
        _logger = logger;
        _clusterService = clusterService;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _centroidStore.InitializeAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogWarning("CentroidSequenceStore initialization was canceled; content sequence detection will use defaults until restart.");
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CentroidSequenceStore initialization failed; content sequence detection will use defaults.");
        }

        if (_clusterService != null)
        {
            _clusterService.ClustersUpdated += OnClustersUpdated;
            _logger.LogDebug("CentroidSequenceStore wired to BotClusterService.ClustersUpdated");
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await _centroidStore.RelearnGlobalAsync(minSessions: 50, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Initial learned-global relearn failed; falling back to suppression");
            }
        });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        if (_clusterService != null)
            _clusterService.ClustersUpdated -= OnClustersUpdated;
        return Task.CompletedTask;
    }

    private void OnClustersUpdated(
        IReadOnlyList<BotCluster> clusters,
        IReadOnlyList<SignatureBehavior> behaviors)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await _centroidStore.RebuildAsync(clusters, CancellationToken.None);
                await _centroidStore.RelearnGlobalAsync(minSessions: 50, CancellationToken.None);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "CentroidSequenceStore rebuild failed after cluster update");
            }
        });
    }
}

/// <summary>Calls AssetHashStore.InitializeAsync on startup to create the SQLite table.</summary>
internal sealed class AssetHashInitHostedService(AssetHashStore store, ILogger<AssetHashInitHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken ct)
    {
        try
        {
            await store.InitializeAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            logger.LogWarning("AssetHashStore initialization was canceled; asset hash matching will use live data only.");
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "AssetHashStore initialization failed; asset hash matching will use live data only.");
        }
    }

    public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
}
