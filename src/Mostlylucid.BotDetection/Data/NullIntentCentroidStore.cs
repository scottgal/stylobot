using Mostlylucid.BotDetection.Data.Contracts;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Sqlite-free no-op intent centroid store. The commercial gateway
///     ships intent classification via pgvector against intent_centroids
///     in the Postgres plugin (when wired). Pre-registering this Null
///     stops <see cref="SqliteIntentCentroidStore"/> from winning the
///     FOSS TryAdd and opening a Sqlite file on disk in a process that
///     should never touch one.
/// </summary>
public sealed class NullIntentCentroidStore : IIntentCentroidStore
{
    public Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IntentCentroidRow>>(Array.Empty<IntentCentroidRow>());

    public Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        => Task.CompletedTask;
}
