namespace Mostlylucid.BotDetection.Data.Contracts;

public interface ISignatureCentroidStore
{
    Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default);
    Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default);
    Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default);
}
