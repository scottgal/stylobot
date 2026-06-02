namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Ephemeral-mode no-op: Leiden clustering still runs per-process against
///     the live in-memory graph, but nothing is persisted across restarts.
///     The dashboard clusters tab repopulates as traffic builds the graph again.
/// </summary>
public sealed class NullClusterStore : IClusterStore
{
    public Task EnsureInitialisedAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<BotCluster>> LoadAllAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<BotCluster>>(Array.Empty<BotCluster>());

    public Task ReplaceAllAsync(IReadOnlyCollection<BotCluster> clusters, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdateLabelAsync(string clusterId, string label, string? description, CancellationToken ct = default)
        => Task.CompletedTask;
}
