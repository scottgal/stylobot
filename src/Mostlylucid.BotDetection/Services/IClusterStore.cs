namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Persistence contract for <see cref="BotCluster"/>. The default FOSS
///     binding is <see cref="SqliteClusterStore"/>; commercial gateways swap
///     in a Postgres implementation via DI replace. Snapshot-replace
///     semantics: <see cref="ReplaceAllAsync"/> is an atomic truncate +
///     bulk-insert in a single transaction so restarts see a consistent
///     cluster set.
/// </summary>
public interface IClusterStore
{
    /// <summary>Idempotent schema/connection bootstrap.</summary>
    Task EnsureInitialisedAsync(CancellationToken ct = default);

    /// <summary>
    ///     Loads every persisted cluster -- called once on
    ///     <see cref="BotClusterService"/> startup to hydrate the in-memory
    ///     snapshot before the first Leiden cycle.
    /// </summary>
    Task<IReadOnlyList<BotCluster>> LoadAllAsync(CancellationToken ct = default);

    /// <summary>
    ///     Atomically replaces the entire persisted cluster set. Called by
    ///     <see cref="BotClusterService"/> after each clustering cycle.
    /// </summary>
    Task ReplaceAllAsync(IReadOnlyCollection<BotCluster> clusters, CancellationToken ct = default);

    /// <summary>
    ///     Update a single cluster's label + description -- the LLM-naming
    ///     write-back path. <paramref name="description"/> is COALESCE'd so
    ///     a null leaves the existing description in place.
    /// </summary>
    Task UpdateLabelAsync(string clusterId, string label, string? description, CancellationToken ct = default);
}
