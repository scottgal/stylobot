using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.UI.Adapters.Remote;

/// <summary>
///     Read-only proxy over <c>/api/v1/sessions</c>. Backs the Sessions tab + the
///     per-signature drill-in. Every write method throws - sessions are the gateway's
///     responsibility to compact and persist; the remote viewer never produces them.
/// </summary>
internal sealed class RemoteSessionStore : ISessionStore
{
    private readonly GatewayApiClient _api;

    public RemoteSessionStore(GatewayApiClient api) => _api = api;

    public async Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default)
    {
        var list = await _api.GetEnvelopeAsync<List<PersistedSession>>(
            $"/api/v1/sessions/{Uri.EscapeDataString(signature)}?limit={limit}", ct);
        return list ?? new List<PersistedSession>();
    }

    public async Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, CancellationToken ct = default)
    {
        var query = isBot is null
            ? $"/api/v1/sessions/recent?limit={limit}"
            : $"/api/v1/sessions/recent?limit={limit}&isBot={isBot.Value.ToString().ToLowerInvariant()}";
        var list = await _api.GetEnvelopeAsync<List<PersistedSession>>(query, ct);
        return list ?? new List<PersistedSession>();
    }

    // === Write surface: not supported on the remote viewer ===

    public Task<long> AddSessionAsync(PersistedSession session, CancellationToken ct = default)
        => throw new NotSupportedException("Session writes are owned by the gateway, not the remote viewer.");

    public Task UpsertSignatureAsync(PersistedSignature signature, CancellationToken ct = default)
        => throw new NotSupportedException("Signature writes are owned by the gateway.");

    public Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default)
        => throw new NotSupportedException("Bucket counters are owned by the gateway.");

    public Task AddRequestAsync(PersistedRequest request, CancellationToken ct = default)
        => throw new NotSupportedException("Request persistence is owned by the gateway.");

    public Task AddRequestBatchAsync(IReadOnlyList<PersistedRequest> requests, CancellationToken ct = default)
        => throw new NotSupportedException("Request batches are owned by the gateway.");

    public Task<List<PersistedRequest>> GetUnatomizedRequestsAsync(int limit = 5000, CancellationToken ct = default)
        => throw new NotSupportedException("Atomization pipeline runs on the gateway.");

    public Task<List<PersistedRequest>> GetRecentRequestsAsync(int limit = 5000, DateTime? sinceUtc = null, CancellationToken ct = default)
        => throw new NotSupportedException("Recent-request retrieval is gateway-internal.");

    public Task LinkRequestsToSessionAsync(long sessionId, IReadOnlyList<long> requestIds, CancellationToken ct = default)
        => throw new NotSupportedException("Atomization pipeline runs on the gateway.");

    public Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default)
        => throw new NotSupportedException("Signature reads via session store run gateway-side.");

    public Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default)
        => throw new NotSupportedException("Top-signature reads run gateway-side.");

    public Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default)
        => throw new NotSupportedException("Session-summary aggregation runs gateway-side.");

    public Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default)
        => throw new NotSupportedException("Session time-series aggregation runs gateway-side.");

    public Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default)
        => throw new NotSupportedException("Country aggregation runs gateway-side.");

    public Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(
        float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default)
        => throw new NotSupportedException("Vector search runs gateway-side.");

    public Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default)
        => throw new NotSupportedException("Entity resolution runs gateway-side.");

    public Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default)
        => throw new NotSupportedException("Entity resolution runs gateway-side.");

    public Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default)
        => throw new NotSupportedException("Entity resolution runs gateway-side.");

    public Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default)
        => throw new NotSupportedException("Entity resolution runs gateway-side.");

    public Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default)
        => throw new NotSupportedException("Entity merges run gateway-side.");

    public Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default)
        => throw new NotSupportedException("Entity updates run gateway-side.");

    public Task PruneAsync(TimeSpan retention, CancellationToken ct = default)
        => throw new NotSupportedException("Session pruning runs gateway-side.");

    public Task PruneBucketsAsync(TimeSpan retention, CancellationToken ct = default)
        => throw new NotSupportedException("Bucket pruning runs gateway-side.");

    public Task<List<(string Signature, int SessionCount)>> GetOverflowingSignaturesAsync(
        int maxPerSignature, int limit = 500, CancellationToken ct = default)
        => throw new NotSupportedException("Compaction priorities run gateway-side.");

    public Task<CompactionResult> CompactSignatureSessionsAsync(string signature, int keepCount, CancellationToken ct = default)
        => throw new NotSupportedException("Session compaction runs gateway-side.");

    public Task<List<CompactionSignatureInfo>> GetSignaturePriorityInfoAsync(List<string> signatures, CancellationToken ct = default)
        => throw new NotSupportedException("Compaction-priority queries run gateway-side.");

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public string? PersistenceConnectionString => null;

    public Task<List<string>> GetActiveEntityIdsAsync(DateTime cutoff, int limit = 100, CancellationToken ct = default)
        => throw new NotSupportedException("Entity scans run gateway-side.");
}
