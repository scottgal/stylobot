using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Ephemeral-mode no-op session store. Every read returns empty, every
///     write is dropped. Session vectorisation still happens per-request in the
///     orchestrator (the contributors keep their own in-process state), but
///     nothing is persisted across restarts -- so cross-session velocity,
///     entity resolution and dashboard timelines are all blank on startup.
///
///     Used by <see cref="Extensions.ServiceCollectionExtensions.AddBotDetectionInMemory"/>.
///     <see cref="PersistenceConnectionString"/> returns null so dependents
///     that open a direct SQLite connection (e.g. CentroidSequenceStore) skip
///     their setup gracefully.
/// </summary>
public sealed class NullSessionStore : ISessionStore
{
    public string? PersistenceConnectionString => null;

    // === Write path ===
    public Task<long> AddSessionAsync(PersistedSession session, CancellationToken ct = default)
        => Task.FromResult(0L);

    public Task UpsertSignatureAsync(PersistedSignature signature, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AddRequestAsync(PersistedRequest request, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AddRequestBatchAsync(IReadOnlyList<PersistedRequest> requests, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<PersistedRequest>> GetUnatomizedRequestsAsync(int limit = 5000, CancellationToken ct = default)
        => Task.FromResult(new List<PersistedRequest>());

    public Task<List<PersistedRequest>> GetRecentRequestsAsync(
        int limit = 5000, DateTime? sinceUtc = null, CancellationToken ct = default)
        => Task.FromResult(new List<PersistedRequest>());

    public Task LinkRequestsToSessionAsync(long sessionId, IReadOnlyList<long> requestIds, CancellationToken ct = default)
        => Task.CompletedTask;

    // === Read path: Sessions ===
    public Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default)
        => Task.FromResult(new List<PersistedSession>());

    public Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, DateTime? since = null, CancellationToken ct = default)
        => Task.FromResult(new List<PersistedSession>());

    // === Read path: Signatures ===
    public Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default)
        => Task.FromResult<PersistedSignature?>(null);

    public Task<string> ResolveSignatureAsync(string requestedSignatureId, CancellationToken ct = default)
        => Task.FromResult(requestedSignatureId);

    public Task RecordSignatureMergeAsync(string oldSignatureId, string newSignatureId, string reason, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default)
        => Task.FromResult(new List<PersistedSignature>());

    // === Read path: Aggregations ===
    public Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default)
        => Task.FromResult(new DashboardSessionSummary());

    public Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default)
        => Task.FromResult(new List<AggregatedBucket>());

    public Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default)
        => Task.FromResult(new List<CountrySessionStats>());

    // === Read path: Vector search ===
    public Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(
        float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default)
        => Task.FromResult(new List<(PersistedSession Session, float Similarity)>());

    // === Entity Resolution ===
    public Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default)
        => Task.FromResult(primarySignature);

    public Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default)
        => Task.FromResult<ResolvedEntity?>(null);

    public Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default)
        => Task.FromResult<ResolvedEntity?>(null);

    public Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default)
        => Task.FromResult(new List<EntityEdge>());

    public Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default) => Task.CompletedTask;

    // === Maintenance ===
    public Task PruneAsync(TimeSpan retention, CancellationToken ct = default) => Task.CompletedTask;
    public Task PruneBucketsAsync(TimeSpan retention, CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<(string Signature, int SessionCount)>> GetOverflowingSignaturesAsync(
        int maxPerSignature, int limit = 500, CancellationToken ct = default)
        => Task.FromResult(new List<(string Signature, int SessionCount)>());

    public Task<CompactionResult> CompactSignatureSessionsAsync(string signature, int keepCount, CancellationToken ct = default)
        => Task.FromResult(new CompactionResult { Signature = signature });

    public Task<List<CompactionSignatureInfo>> GetSignaturePriorityInfoAsync(
        List<string> signatures, CancellationToken ct = default)
        => Task.FromResult(new List<CompactionSignatureInfo>());

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<List<string>> GetActiveEntityIdsAsync(DateTime cutoff, int limit = 100, CancellationToken ct = default)
        => Task.FromResult(new List<string>());
}
