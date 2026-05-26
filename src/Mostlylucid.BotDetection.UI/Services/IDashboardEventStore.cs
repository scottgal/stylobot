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
    /// </summary>
    Task<DashboardSummary> GetSummaryAsync();

    /// <summary>
    ///     Get time-series data for charts.
    /// </summary>
    Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime,
        DateTime endTime,
        TimeSpan bucketSize);

    /// <summary>
    ///     Get top bot signatures ordered by hit count descending.
    ///     When startTime/endTime are provided, only detections within that range are considered.
    /// </summary>
    Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>
    ///     Get country-level statistics (total requests, bot count, bot rate).
    ///     When startTime/endTime are provided, only detections within that range are considered.
    /// </summary>
    Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>
    ///     Get detailed statistics for a single country, including bot type and signature breakdowns.
    /// </summary>
    Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>
    ///     Get endpoint-level statistics aggregated by method + path.
    /// </summary>
    Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null);

    /// <summary>
    ///     Get detailed statistics for a single endpoint.
    /// </summary>
    Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null);

    /// <summary>
    ///     Get recent threat activity (CVE probes, honeypot engagements, high threat-score detections).
    /// </summary>
    Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null);

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
}
