using Mostlylucid.BotDetection.Data.Contracts;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     No-op signature centroid store. Registered by hosts that intentionally
///     keep the FOSS Sqlite slim-similarity store out of the process -- the
///     commercial gateway, for example, runs the similarity surface against
///     pgvector in <see cref="Mostlylucid.BotDetection"/>'s commercial Postgres
///     plugin and must not silently fall back to a Sqlite file on disk if the
///     pgvector wiring is absent. Writes are dropped on the floor; reads
///     return empty so <see cref="Similarity.SlimSignatureSimilaritySearch"/>
///     simply has nothing to suggest -- never throws, never opens a file.
/// </summary>
public sealed class NullSignatureCentroidStore : ISignatureCentroidStore
{
    public Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<SignatureCentroidRow>>(Array.Empty<SignatureCentroidRow>());

    public Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
        => Task.CompletedTask;
}
