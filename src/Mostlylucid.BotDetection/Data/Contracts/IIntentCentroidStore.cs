namespace Mostlylucid.BotDetection.Data.Contracts;

public interface IIntentCentroidStore
{
    Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default);
    Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default);
    Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default);

    /// <summary>
    ///     Non-blocking hot-path facade. Called by <c>SlimIntentSearch.AddAsync</c>.
    ///     Default is a no-op so null stores and test fakes compile without change.
    ///     <c>SqliteIntentCentroidStore</c> overrides with <c>WriteBehindLfuStore.Record</c>.
    /// </summary>
    void RecordIntent(string signatureId, float[] vector, double threatScore, string category) { }
}
