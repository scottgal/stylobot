using Mostlylucid.BotDetection.Data.Contracts;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Sqlite-free no-op session centroid store. Sister of
///     <see cref="NullSignatureCentroidStore"/> for the session-shape slim
///     similarity surface; commercial gateways register this so the FOSS
///     Sqlite implementation never wins the DI TryAdd race. Reads return
///     empty, writes drop -- the commercial similarity path lives on
///     pgvector via the Postgres plugin.
/// </summary>
public sealed class NullSessionCentroidStore : ISessionCentroidStore
{
    public Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SessionCentroidRow>>(Array.Empty<SessionCentroidRow>());

    public Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        => Task.CompletedTask;
}
