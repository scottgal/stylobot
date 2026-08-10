using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Storage for dashboard events (detections and signatures).
/// </summary>
public interface IDashboardEventStore
{
    /// <summary>
    ///     Add a detection event to the store.
    /// </summary>
    Task AddDetectionAsync(DashboardDetectionEvent detection);

    /// <summary>
    ///     Add or update a signature observation in the store.
    ///     Returns the signature with updated hit_count after upsert.
    /// </summary>
    Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature);

    /// <summary>
    ///     Updates the persisted bot name + description on the signature row, after the
    ///     async LLM-naming pipeline produces a richer name than the original UA-derived
    ///     one. Called from <c>ILlmResultCallback.OnSignatureDescriptionAsync</c>; pairs
    ///     with <c>SqliteFingerprintStore.UpdateDisplayNameForSignatureAsync</c> so both
    ///     persistent stores stay in sync. No-op when the signature isn't present.
    /// </summary>
    Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default);

    /// <summary>
    ///     Get recent detections with optional filtering.
    /// </summary>
    Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default);

    /// <summary>
    ///     Get recent signatures.
    /// </summary>
    Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null);

    /// <summary>
    ///     Get summary statistics.
    ///     When both <paramref name="startTime"/> and <paramref name="endTime"/> are null the window
    ///     defaults to the last 6 hours (preserving legacy behavior). When set, the query is bounded to
    ///     [startTime, endTime]. <paramref name="audienceFilter"/> ("bots" | "humans" | null) restricts
    ///     the detection-level counts (TotalRequests, BotRequests, HumanRequests, bytes/p95); the
    ///     fingerprint-level sub-query remains unfiltered.
    /// </summary>
    Task<DashboardSummary> GetSummaryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? audienceFilter = null,
        IReadOnlyList<string>? domains = null);

    /// <summary>
    ///     Get time-series data for charts.
    ///     <paramref name="audienceFilter"/> ("bots" | "humans" | null) restricts each bucket to the
    ///     matching audience.
    ///     <paramref name="domains"/> restricts to detections whose <c>domain</c> column matches one
    ///     of the given values (null / empty = no filter).
    /// </summary>
    Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime,
        DateTime endTime,
        TimeSpan bucketSize,
        string? audienceFilter = null,
        IReadOnlyList<string>? domains = null);

    /// <summary>
    ///     Get top bot signatures ordered by hit count descending.
    ///     When startTime/endTime are provided, only detections within that range are considered.
    ///     <paramref name="domains"/> restricts to detections whose <c>domain</c> column matches one
    ///     of the given values (null / empty = no filter).
    /// </summary>
    Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null);

    /// <summary>
    ///     Get authoritative visitor segment counts (All/Humans/Bots/AI/Tools/Internal)
    ///     using the store's classification logic. Returns true totals, not capped.
    ///     Used by both dashboard summary and filter tabs to ensure consistent counts.
    /// </summary>
    async Task<FilterCounts> GetVisitorSegmentCountsAsync(
        DateTime startTime, DateTime endTime,
        string? filter = null, string? country = null, string? botType = null,
        string? threat = null, IReadOnlyList<string>? domains = null)
    {
        await Task.CompletedTask;
        return new FilterCounts { All = 0, Humans = 0, Bots = 0, Ai = 0, Tools = 0, Internal = 0 };
    }

    /// <summary>
    ///     Raw per-host domain statistics: one row per distinct observed domain, ordered by
    ///     request count descending and capped at <paramref name="limit"/>. Returns ALL observed
    ///     domains including internal self-traffic (flagged via <see cref="DashboardDomainStat.IsInternal"/>,
    ///     never excluded) — the licensed-vs-pool split is the commercial overlay's job, not the store's.
    ///     Default returns empty so in-memory / test fakes need no override; the SQLite and PostgreSQL
    ///     stores supply the real aggregate.
    /// </summary>
    async Task<IReadOnlyList<DashboardDomainStat>> GetDomainStatsAsync(
        DateTime? startTime = null, DateTime? endTime = null, int limit = 200, CancellationToken ct = default)
    {
        await Task.CompletedTask;
        return [];
    }

    /// <summary>
    ///     Get country-level statistics (total requests, bot count, bot rate).
    ///     When startTime/endTime are provided, only detections within that range are considered.
    ///     <paramref name="domains"/> restricts to detections whose <c>domain</c> column matches one
    ///     of the given values (null / empty = no filter).
    /// </summary>
    Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null);

    /// <summary>
    ///     Get detailed statistics for a single country, including bot type and signature breakdowns.
    /// </summary>
    Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>
    ///     Look up a single signature by its primary-signature id from the
    ///     <c>signatures</c> table directly — no limit, no recency window.
    ///     Returns null when the signature has never been seen. Used by the
    ///     signature-detail page's empty-detections fallback to avoid the
    ///     top-N recency window that can hide a high-hit but stale-last_seen
    ///     signature (it appears in Top Bots by hit count but falls outside
    ///     GetSignaturesAsync's last_seen DESC limit).
    /// </summary>
    Task<DashboardSignatureEvent?> TryGetSignatureAsync(string signatureId, CancellationToken ct = default)
    {
        return Task.FromResult<DashboardSignatureEvent?>(null);
    }

    /// <summary>
    ///     Upsert a single detection's endpoint stats into the bounded
    ///     <c>endpoint_stats</c> table — one row per (method, path, domain),
    ///     counters incremented atomically. Called from
    ///     <see cref="AddDetectionAsync"/> so the endpoint list is maintained
    ///     at detection-persist time rather than scanned from the per-request
    ///     detections table. The default implementation is a no-op (other
    ///     store implementations that don't have an endpoint_stats table).
    /// </summary>
    Task UpsertEndpointStatsAsync(string method, string path, string? domain,
        bool isBot, double botProbability, double? threatScore,
        long? responseBytes, double? processingTimeMs,
        int statusCode, int? upstreamStatusCode,
        DateTime timestamp,
        CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Get endpoint-level statistics aggregated by method + path.
    /// </summary>
    Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null);

    /// <summary>
    ///     Per-endpoint stats restricted to a single signature, grouped by
    ///     (method, path) and ordered by hit count descending. Powers the
    ///     Endpoints Visited table on the signature detail page.
    ///     <para>
    ///     Returns an empty list when the signature is unknown or has no
    ///     persisted detections yet. P95 is approximated on SQLite via
    ///     <c>avg + 0.9 * (max - avg)</c>; the Postgres mirror returns the real
    ///     percentile.
    ///     </para>
    /// </summary>
    Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(
        string signature,
        int topN = 25,
        CancellationToken ct = default);

    /// <summary>
    ///     Get detailed statistics for a single endpoint.
    /// </summary>
    Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>
    ///     Get recent threat activity (CVE probes, honeypot engagements, high threat-score detections).
    /// </summary>
    Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null);

    /// <summary>
    ///     Search detections by raw User-Agent substring match.
    /// </summary>
    Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20);

    /// <summary>
    ///     Time-series of how a given UA family's versions have been distributed over the
    ///     past <paramref name="hours"/>. Reads the <c>ua.family</c> and <c>ua.family_version</c>
    ///     entries on the existing per-detection <c>important_signals</c> JSON column -- no
    ///     new storage. Each row is one (hour bucket, version) bin with a hit count so the
    ///     dashboard can render a stacked-area chart and spot outliers / version churn.
    ///     Returns an empty list when the store doesn't track per-detection signals (FOSS
    ///     SQLite path).
    /// </summary>
    Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(
        string family, int hours = 168, CancellationToken ct = default);

    /// <summary>
    ///     Aggregate honeypot hits per distinct path, for the dashboard Honeypot subtab.
    ///     Rows are derived from detections where the action policy was
    ///     <c>honeypot-response</c> or <c>simulation-pack</c> in the time window.
    /// </summary>
    Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(
        int count = 50, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken ct = default);

    /// <summary>Unified investigation query -- filter by any entity type, get cross-associated results.</summary>
    Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default);

    /// <summary>Deletes detection records older than the specified cutoff. Returns count pruned.</summary>
    Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default);

    /// <summary>
    ///     Compression fold (temporal-store absorption): nulls the per-request
    ///     detail columns on aged low-importance detection rows, in importance
    ///     order, in batches of at most <paramref name="batchSize"/>. Two passes:
    ///     (1) rows older than <paramref name="hotCutoff"/> whose stored
    ///     importance weight is below <paramref name="importanceFloor"/>;
    ///     (2) rows older than <paramref name="fullAbsorptionCutoff"/> regardless
    ///     of importance (full absorption — old rows are summaries of themselves).
    ///     The classification/aggregate columns (counts, bot probability, risk
    ///     band, action, threat, domain) are never touched, so dashboard reads
    ///     return identical shapes with or without compression; only the verbose
    ///     per-request detail ages out. Idempotent — folding an already-folded
    ///     row is a no-op. Returns the number of rows folded this call.
    ///     <para>
    ///     Default implementation returns 0: FOSS SQLite implements the fold;
    ///     other store implementations own their own fold (the default keeps
    ///     fakes and non-SQLite stores compiling unchanged).
    ///     </para>
    /// </summary>
    Task<int> FoldAgedDetectionsAsync(
        DateTime hotCutoff,
        DateTime fullAbsorptionCutoff,
        double importanceFloor,
        int batchSize,
        CancellationToken ct = default) => Task.FromResult(0);

    /// <summary>
    ///     Fetch every widget's data for a page in a single call.
    ///     Only the <see cref="Models.DatasetKind"/>s listed in
    ///     <paramref name="request"/>.<see cref="Models.DashboardBatchRequest.Datasets"/> are fetched;
    ///     the corresponding bundle properties are null for any kind not requested.
    ///     Default implementation fans out to the existing per-widget <c>Get*Async</c>
    ///     methods via <see cref="DashboardEventStoreBatchDefaults.FanOutAsync"/>;
    ///     Postgres overrides with a single windowed-CTE scan.
    /// </summary>
    Task<Models.DashboardDatasetBundle> ComposeBatchAsync(
        Models.DashboardBatchRequest request, CancellationToken ct = default)
        => DashboardEventStoreBatchDefaults.FanOutAsync(this, request, ct);

    /// <summary>
    ///     Append a single <see cref="DegradationSnapshot"/> sampled from
    ///     <see cref="DegradationAtom"/> at <c>ScheduleCoordinator.Tick10s</c>.
    ///     Persists across restart per <c>feedback_no_inmemory_stores</c> --
    ///     the prior <c>DegradationHistoryAtom</c> ring lost the entire window
    ///     on every reboot. Backs the Traffic page's site-health chartlet.
    /// </summary>
    /// <remarks>
    ///     Append-only; rows older than the configured retention are pruned by
    ///     the same retention sweep that prunes detections.
    /// </remarks>
    Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default);

    /// <summary>
    ///     Return persisted <see cref="DegradationSnapshot"/> rows whose
    ///     timestamp falls in <c>[startTime, endTime]</c>, ordered oldest-first.
    ///     Empty list when no samples landed in the window.
    /// </summary>
    Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(
        DateTime startTime, DateTime endTime, CancellationToken ct = default);
}
