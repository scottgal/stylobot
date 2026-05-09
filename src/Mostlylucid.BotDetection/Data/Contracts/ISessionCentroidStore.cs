namespace Mostlylucid.BotDetection.Data.Contracts;

public interface ISessionCentroidStore
{
    Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default);
    Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default);
    Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default);
}
