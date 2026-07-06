using Mostlylucid.Ephemeral.Atoms.Llm;

namespace Mostlylucid.BotDetection.Services.Llm;

/// <summary>
///     Persists a cluster-naming result by writing through to
///     <see cref="BotClusterService.UpdateClusterDescription"/> (snapshot replace +
///     SQLite write-through) and pushing the live update via
///     <see cref="IClusterDescriptionCallback.OnClusterDescriptionUpdatedAsync"/> —
///     the same two sinks the legacy
///     <c>LlmDescriptionCoordinator.ProcessClusterAsync</c> hit. Always releases the
///     in-flight reservation in <c>finally</c> so a writeback exception still frees
///     the key for the next tick. The picker is also told to forget the cluster id
///     so a subsequent tick doesn't surface it again. Invoker-side failures (where
///     the EphemeralLlmCoordinator skips writeback entirely) are reclaimed by
///     <see cref="ClusterInFlightSet"/>'s staleness window.
/// </summary>
public sealed class ClusterLlmWriteback : IEphemeralWriteback<ClusterPickItem, ClusterNamingResult>
{
    private readonly ClusterInFlightSet _inFlight;
    private readonly NeedsDescriptionClusterPicker _picker;
    private readonly BotClusterService _clusterService;
    private readonly IClusterDescriptionCallback? _clusterCallback;

    public ClusterLlmWriteback(
        ClusterInFlightSet inFlight,
        NeedsDescriptionClusterPicker picker,
        BotClusterService clusterService,
        IClusterDescriptionCallback? clusterCallback = null)
    {
        _inFlight = inFlight;
        _picker = picker;
        _clusterService = clusterService;
        _clusterCallback = clusterCallback;
    }

    public async Task ApplyAsync(ClusterPickItem item, ClusterNamingResult result, CancellationToken ct)
    {
        try
        {
            _clusterService.UpdateClusterDescription(
                item.ClusterId,
                result.Name,
                result.Description ?? string.Empty);

            if (_clusterCallback is not null)
            {
                await _clusterCallback.OnClusterDescriptionUpdatedAsync(
                    item.ClusterId,
                    result.Name,
                    result.Description ?? string.Empty,
                    ct);
            }

            _picker.Forget(item.ClusterId);
        }
        finally
        {
            _inFlight.Release(item.ClusterId);
        }
    }
}
