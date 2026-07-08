namespace Mostlylucid.BotDetection.Data.Contracts;

public interface ISignatureCentroidStore
{
    Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default);
    Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default);
    Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default);

    /// <summary>
    ///     Non-blocking hot-path facade. Called by <c>SlimSignatureSimilaritySearch.AddAsync</c>
    ///     on every request. Default is a no-op so null stores and test fakes that do not
    ///     override it compile without change. <c>SqliteSignatureCentroidStore</c> overrides
    ///     with the <c>WriteBehindLfuStore.Record</c> path (zero-alloc, no Task.Run).
    /// </summary>
    void RecordSignature(string signatureId, float[] vector, bool wasBot, double confidence) { }
}
