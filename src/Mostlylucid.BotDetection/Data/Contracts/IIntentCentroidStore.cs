namespace Mostlylucid.BotDetection.Data.Contracts;

public interface IIntentCentroidStore
{
    Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default);
    Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default);
    Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default);
}
