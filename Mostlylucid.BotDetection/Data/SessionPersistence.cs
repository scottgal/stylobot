using Mostlylucid.BotDetection.Analysis;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Persisted session record. Sessions are the unit of storage - not individual requests.
///     Each session captures a compressed behavioral snapshot (Markov vector + fingerprint),
///     summary stats, and the dominant detection outcome.
///     This replaces per-request event storage (TimescaleDB) with session-level compression,
///     reducing storage by ~100x while preserving behavioral intelligence.
/// </summary>
public sealed record PersistedSession
{
    /// <summary>Auto-increment row ID</summary>
    public long Id { get; init; }

    /// <summary>Client signature (hashed IP:UA - zero PII)</summary>
    public required string Signature { get; init; }

    /// <summary>When this session started</summary>
    public required DateTime StartedAt { get; init; }

    /// <summary>When this session ended</summary>
    public required DateTime EndedAt { get; init; }

    /// <summary>Number of requests in this session</summary>
    public required int RequestCount { get; init; }

    /// <summary>Session vector as byte[] (float[] serialized for SQLite BLOB / PostgreSQL vector)</summary>
    public required byte[] Vector { get; init; }

    /// <summary>Vector maturity score (0-1)</summary>
    public required float Maturity { get; init; }

    /// <summary>Dominant Markov state (e.g., "PageView", "ApiCall")</summary>
    public required string DominantState { get; init; }

    /// <summary>Was the session classified as bot?</summary>
    public required bool IsBot { get; init; }

    /// <summary>Average bot probability across the session</summary>
    public required double AvgBotProbability { get; init; }

    /// <summary>Average confidence across the session</summary>
    public required double AvgConfidence { get; init; }

    /// <summary>Risk band at session end</summary>
    public required string RiskBand { get; init; }

    /// <summary>Action taken (block, throttle-stealth, Allow, etc.)</summary>
    public string? Action { get; init; }

    /// <summary>Bot name if identified</summary>
    public string? BotName { get; init; }

    /// <summary>Bot type if identified</summary>
    public string? BotType { get; init; }

    /// <summary>Country code (2-letter ISO)</summary>
    public string? CountryCode { get; init; }

    /// <summary>Top detection reasons (JSON array)</summary>
    public string? TopReasonsJson { get; init; }

    /// <summary>
    ///     Markov transition counts as JSON: {"PageView->ApiCall": 5, "ApiCall->PageView": 3, ...}
    ///     Enables drill-in visualization of request chains without storing individual requests.
    /// </summary>
    public string? TransitionCountsJson { get; init; }

    /// <summary>Distinct paths visited in this session (JSON array, templatized)</summary>
    public string? PathsJson { get; init; }

    /// <summary>Average processing time in ms</summary>
    public double AvgProcessingTimeMs { get; init; }

    /// <summary>Count of 4xx responses in this session</summary>
    public int ErrorCount { get; init; }

    /// <summary>Timing entropy (low = bot-like regularity)</summary>
    public float TimingEntropy { get; init; }

    /// <summary>Narrative summary</summary>
    public string? Narrative { get; init; }

    /// <summary>
    ///     HMAC hashes of discriminatory HTTP headers at session time.
    ///     JSON: {"accept-language": "hash1", "sec-ch-ua": "hash2", "_header_order": "hash3"}.
    ///     Used for retroactive stability analysis per entity.
    /// </summary>
    public string? HeaderHashesJson { get; init; }

    /// <summary>
    ///     PII-stripped raw User-Agent string from the session's first request.
    ///     Emails, credential URLs, and phone numbers are redacted before storage.
    /// </summary>
    public string? UserAgentRaw { get; init; }

    /// <summary>
    ///     Frequency fingerprint BLOB: 8-element float[] serialized as raw bytes.
    ///     Autocorrelation at [1s, 3s, 10s, 30s, 1m, 3m, 10m, 30m] lag scales.
    ///     Null when computed from fewer than 3 requests.
    ///     Loaded from SQLite to seed the HNSW index with rhythm metadata.
    /// </summary>
    public byte[]? FrequencyFingerprintBlob { get; init; }

    /// <summary>
    ///     Drift vector BLOB: 129-element float[] serialized as raw bytes.
    ///     Linear regression slope over the preceding N session vectors.
    ///     Null for the first session or when fewer than 3 prior sessions exist.
    ///     Preserved through HNSW compaction to show where a campaign was heading.
    /// </summary>
    public byte[]? DriftVectorBlob { get; init; }
}

/// <summary>
///     Persisted signature reputation. Accumulated across all sessions.
///     This is the long-lived identity - sessions come and go, signatures persist.
/// </summary>
public sealed record PersistedSignature
{
    /// <summary>Signature ID (hashed, zero PII)</summary>
    public required string SignatureId { get; init; }

    /// <summary>Total sessions observed</summary>
    public required int SessionCount { get; init; }

    /// <summary>Total requests across all sessions</summary>
    public required int TotalRequestCount { get; init; }

    /// <summary>First seen timestamp</summary>
    public required DateTime FirstSeen { get; init; }

    /// <summary>Last seen timestamp</summary>
    public required DateTime LastSeen { get; init; }

    /// <summary>Is this a known bot?</summary>
    public required bool IsBot { get; init; }

    /// <summary>Latest bot probability</summary>
    public required double BotProbability { get; init; }

    /// <summary>Latest confidence</summary>
    public required double Confidence { get; init; }

    /// <summary>Latest risk band</summary>
    public required string RiskBand { get; init; }

    /// <summary>Bot name if identified</summary>
    public string? BotName { get; init; }

    /// <summary>Bot type</summary>
    public string? BotType { get; init; }

    /// <summary>Latest action</summary>
    public string? Action { get; init; }

    /// <summary>Dominant country code</summary>
    public string? CountryCode { get; init; }

    /// <summary>
    ///     Compacted root vector: maturity-weighted average of all past session vectors.
    ///     Represents the signature's baseline behavioral profile.
    /// </summary>
    public byte[]? RootVector { get; init; }

    /// <summary>Root vector maturity</summary>
    public float RootVectorMaturity { get; init; }

    /// <summary>Narrative description</summary>
    public string? Narrative { get; init; }

    /// <summary>Top reasons (JSON)</summary>
    public string? TopReasonsJson { get; init; }
}

/// <summary>
///     Aggregated counter row for time-series charts.
///     Instead of storing every request, we maintain per-bucket counters.
///     Buckets are 1-minute granularity, rolled up to 1-hour and 1-day on query.
/// </summary>
public sealed record AggregatedBucket
{
    /// <summary>Bucket start time (truncated to minute)</summary>
    public required DateTime BucketTime { get; init; }

    /// <summary>Total requests in this bucket</summary>
    public required int TotalCount { get; init; }

    /// <summary>Bot requests in this bucket</summary>
    public required int BotCount { get; init; }

    /// <summary>Human requests in this bucket</summary>
    public required int HumanCount { get; init; }

    /// <summary>Unique signatures in this bucket</summary>
    public required int UniqueSignatures { get; init; }

    /// <summary>Sessions started in this bucket</summary>
    public required int SessionsStarted { get; init; }

    /// <summary>Average processing time in this bucket</summary>
    public required double AvgProcessingTimeMs { get; init; }
}

/// <summary>
///     Session-based event store interface. Sessions are the unit of storage.
///     Replaces per-request event storage with session-level compression.
///     Implementations:
///     - SQLite (core, zero-dependency, default)
///     - PostgreSQL + pgvector (commercial, for vector similarity at scale)
/// </summary>
public interface ISessionStore
{
    // === Write path ===

    /// <summary>Persist a completed session snapshot.</summary>
    Task AddSessionAsync(PersistedSession session, CancellationToken ct = default);

    /// <summary>Upsert a signature (create or update hit counts/stats).</summary>
    Task UpsertSignatureAsync(PersistedSignature signature, CancellationToken ct = default);

    /// <summary>Increment aggregated counters for a time bucket.</summary>
    Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default);

    /// <summary>
    ///     Update (or create) a signature's detection classification after a request is processed.
    ///     Called per-request by the orchestrator so bot probability and risk data are persisted
    ///     immediately, not only when a session boundary is detected.
    /// </summary>
    Task UpdateSignatureDetectionAsync(
        string signatureId,
        bool isBot,
        double botProbability,
        double confidence,
        string riskBand,
        string? botName,
        string? botType,
        string? action,
        DateTime requestTime,
        CancellationToken ct = default);

    /// <summary>Persist a single request record immediately after detection.</summary>
    Task AddRequestAsync(PersistedRequest request, CancellationToken ct = default);

    /// <summary>Batch insert for coordinator write path.</summary>
    Task AddRequestBatchAsync(IReadOnlyList<PersistedRequest> requests, CancellationToken ct = default);

    /// <summary>Get all requests not yet assigned to a session, up to limit rows, oldest-first per signature.</summary>
    Task<List<PersistedRequest>> GetUnatomizedRequestsAsync(int limit = 5000, CancellationToken ct = default);

    /// <summary>Link a list of request IDs to a session row.</summary>
    Task LinkRequestsToSessionAsync(long sessionId, IReadOnlyList<long> requestIds, CancellationToken ct = default);

    // === Read path: Sessions ===

    /// <summary>Get sessions for a signature, ordered by most recent.</summary>
    Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default);

    /// <summary>Get recent sessions across all signatures.</summary>
    Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, CancellationToken ct = default);

    // === Read path: Signatures ===

    /// <summary>Get a single signature by ID.</summary>
    Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default);

    /// <summary>Get top signatures by session count.</summary>
    Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default);

    // === Read path: Aggregations ===

    /// <summary>Get summary stats (total sessions, bots, humans, unique signatures).</summary>
    Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default);

    /// <summary>Get time-series buckets for charts.</summary>
    Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default);

    /// <summary>Get country-level stats from session data.</summary>
    Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default);

    // === Read path: Vector search ===

    /// <summary>Find sessions similar to a query vector (cosine similarity).</summary>
    Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(
        float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default);

    // === Entity Resolution ===

    /// <summary>Resolve or create entity for a PrimarySignature. Returns the entity ID.</summary>
    Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default);

    /// <summary>Get the entity for a PrimarySignature (null if not yet resolved).</summary>
    Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default);

    /// <summary>Get an entity by ID.</summary>
    Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default);

    /// <summary>Get all active edges for an entity.</summary>
    Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default);

    /// <summary>Create a merge edge linking a signature to an existing entity.</summary>
    Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default);

    /// <summary>Update entity metadata (confidence level, rotation cadence, stable anchors, etc.).</summary>
    Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default);

    // === Maintenance ===

    /// <summary>Delete sessions older than retention period.</summary>
    Task PruneAsync(TimeSpan retention, CancellationToken ct = default);

    /// <summary>Delete time-series buckets older than the given retention period.</summary>
    Task PruneBucketsAsync(TimeSpan retention, CancellationToken ct = default);

    /// <summary>
    ///     Get signatures that have more than <paramref name="maxPerSignature"/> session rows.
    ///     Returns (signature, sessionCount) pairs ordered by session count descending.
    /// </summary>
    Task<List<(string Signature, int SessionCount)>> GetOverflowingSignaturesAsync(
        int maxPerSignature, int limit = 500, CancellationToken ct = default);

    /// <summary>
    ///     Compact old sessions for a signature into its root_vector (maturity-weighted centroid).
    ///     Keeps the most recent <paramref name="keepCount"/> sessions at full resolution.
    ///     Also computes and stores the velocity centroid (average drift direction) across compacted sessions.
    ///     Returns the new behavioral centroid and velocity centroid for HNSW update.
    /// </summary>
    Task<CompactionResult> CompactSignatureSessionsAsync(
        string signature, int keepCount, CancellationToken ct = default);

    /// <summary>
    ///     Get priority metadata for a set of signatures.
    ///     Used by VectorCompactionService to decide compaction order.
    /// </summary>
    Task<List<CompactionSignatureInfo>> GetSignaturePriorityInfoAsync(
        List<string> signatures, CancellationToken ct = default);

    /// <summary>Initialize schema (create tables if needed).</summary>
    Task InitializeAsync(CancellationToken ct = default);

    // === Persistence metadata ===

    /// <summary>
    ///     Connection string for this persistence backend, used by stores that open
    ///     a direct connection (e.g., CentroidSequenceStore, AssetHashStore).
    ///     Returns null for implementations that do not expose a connection string.
    /// </summary>
    string? PersistenceConnectionString { get; }

    /// <summary>
    ///     Get entity IDs that have active edges and were updated since <paramref name="cutoff"/>.
    ///     Used by EntityResolutionService for periodic analysis.
    /// </summary>
    Task<List<string>> GetActiveEntityIdsAsync(DateTime cutoff, int limit = 100, CancellationToken ct = default);
}

/// <summary>Summary stats for the session-based dashboard.</summary>
public sealed record DashboardSessionSummary
{
    public int TotalSessions { get; init; }
    public int BotSessions { get; init; }
    public int HumanSessions { get; init; }
    public int UniqueSignatures { get; init; }
    public int TotalRequests { get; init; }
    public double AvgProcessingTimeMs { get; init; }
    public DateTime? LastActivityAt { get; init; }
}

/// <summary>Result of compacting a signature's sessions into a behavioral centroid.</summary>
public sealed record CompactionResult
{
    /// <summary>The compacted signature.</summary>
    public required string Signature { get; init; }

    /// <summary>Maturity-weighted behavioral centroid of all compacted sessions.</summary>
    public float[]? BehavioralCentroid { get; init; }

    /// <summary>Average velocity vector across consecutive session pairs (drift direction).</summary>
    public float[]? VelocityCentroid { get; init; }

    /// <summary>Mean frequency fingerprint across compacted sessions (8D autocorrelation centroid).</summary>
    public float[]? FrequencyCentroid { get; init; }

    /// <summary>Number of sessions that were compacted.</summary>
    public int CompactedCount { get; init; }

    /// <summary>Weighted average maturity of the compacted sessions.</summary>
    public float CentroidMaturity { get; init; }

    /// <summary>Whether compaction produced a valid centroid.</summary>
    public bool HasCentroid => BehavioralCentroid is { Length: > 0 };
}

/// <summary>Priority metadata for a signature, used by VectorCompactionService.</summary>
public sealed record CompactionSignatureInfo
{
    public required string Signature { get; init; }
    public required int SessionCount { get; init; }
    public required double BotProbability { get; init; }
    public required string RiskBand { get; init; }
    public required bool IsBot { get; init; }
    public required bool HasEntityMapping { get; init; }
    public required DateTime LastSeen { get; init; }

    /// <summary>
    ///     Priority score: risk × recency_decay × bot_probability × entity_bonus.
    ///     High priority = keep at full resolution. Low priority = compact first.
    /// </summary>
    public double Priority =>
        RiskScore(RiskBand)
        * Math.Exp(-(DateTime.UtcNow - LastSeen).TotalDays / 30.0)
        * Math.Max(BotProbability, 0.1)
        * (HasEntityMapping ? 1.0 : 0.5);

    private static double RiskScore(string band) => band switch
    {
        "Critical" => 1.0,
        "High" => 0.8,
        "Medium" => 0.5,
        "Low" => 0.2,
        _ => 0.1
    };
}

/// <summary>Country-level stats aggregated from sessions.</summary>
public sealed record CountrySessionStats
{
    public required string CountryCode { get; init; }
    public int TotalSessions { get; init; }
    public int BotSessions { get; init; }
    public int HumanSessions { get; init; }
    public int TotalRequests { get; init; }
    public double BotRate => TotalSessions > 0 ? (double)BotSessions / TotalSessions : 0;
}