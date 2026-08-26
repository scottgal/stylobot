using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Helpers;

/// <summary>
///     Minimal <see cref="IDashboardEventStore"/> double for boot / composition
///     tests that need a working dashboard pipeline WITHOUT touching SQLite.
///     Every read returns empty; every write is a no-op. Used by the
///     <c>E2E</c> tests (Prometheus optionality + UI package boot smoke) so the
///     host builds and serves with no .db files.
/// </summary>
public sealed class NullDashboardEventStore : IDashboardEventStore
{
    public Task AddDetectionAsync(DashboardDetectionEvent detection) => Task.CompletedTask;

    public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
        => Task.FromResult(signature);

    public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
        => Task.FromResult(new List<DashboardDetectionEvent>());

    public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null)
        => Task.FromResult(new List<DashboardSignatureEvent>());

    public Task<DashboardSummary> GetSummaryAsync(
        DateTime? startTime = null, DateTime? endTime = null,
        string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        => Task.FromResult(new DashboardSummary
        {
            Timestamp = DateTime.UtcNow,
            TotalRequests = 0,
            BotRequests = 0,
            HumanRequests = 0,
            UncertainRequests = 0,
            RiskBandCounts = new Dictionary<string, int>(),
            TopBotTypes = new Dictionary<string, int>(),
            TopActions = new Dictionary<string, int>(),
            UniqueSignatures = 0,
        });

    public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime, DateTime endTime, TimeSpan bucketSize,
        string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        => Task.FromResult(new List<DashboardTimeSeriesPoint>());

    public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
        int count = 10, DateTime? startTime = null, DateTime? endTime = null,
        string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        => Task.FromResult(new List<DashboardTopBotEntry>());

    public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
        int count = 20, DateTime? startTime = null, DateTime? endTime = null,
        string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        => Task.FromResult(new List<DashboardCountryStats>());

    public Task<DashboardCountryDetail?> GetCountryDetailAsync(
        string countryCode, DateTime? startTime = null, DateTime? endTime = null)
        => Task.FromResult<DashboardCountryDetail?>(null);

    public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
        int count = 50, DateTime? startTime = null, DateTime? endTime = null,
        string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        => Task.FromResult(new List<DashboardEndpointStats>());

    public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(
        string signature, int topN = 25, CancellationToken ct = default)
        => Task.FromResult(new List<SignatureEndpointStats>());

    public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(
        string method, string path, DateTime? startTime = null, DateTime? endTime = null)
        => Task.FromResult<DashboardEndpointDetail?>(null);

    public Task<List<ThreatEntry>> GetThreatsAsync(
        int count = 20, DateTime? startTime = null, DateTime? endTime = null,
        IReadOnlyList<string>? domains = null)
        => Task.FromResult(new List<ThreatEntry>());

    public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20)
        => Task.FromResult(new List<UserAgentSearchResult>());

    public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(
        string family, int hours = 168, CancellationToken ct = default)
        => Task.FromResult(new List<UserAgentVersionBucket>());

    public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(
        int count = 50, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken ct = default)
        => Task.FromResult(new List<HoneypotHitRow>());

    public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default)
        => Task.FromResult(new InvestigationResult { Summary = new InvestigationSummary() });

    public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(
        DateTime startTime, DateTime endTime, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<DegradationSnapshot>>([]);
}
