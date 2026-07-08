namespace Mostlylucid.BotDetection.Data.Contracts;

public interface ISessionCentroidStore
{
    Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default);
    Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default);
    Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default);

    /// <summary>
    ///     Non-blocking hot-path facade. Called by <c>SlimSessionVectorSearch.AddAsync</c>.
    ///     Default is a no-op so null stores and test fakes compile without change.
    ///     <c>SqliteSessionCentroidStore</c> overrides with <c>WriteBehindLfuStore.Record</c>.
    /// </summary>
    void RecordSession(SessionCentroidRow row) { }
}
