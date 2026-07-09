using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Site-health into the batch: the traffic manifest now carries "site-health"
///     (DatasetKind.DegradationHistory), the fan-out default composes it, and the
///     page bundle exposes the Degradations slice — so the SbSiteHealth VC reads warm
///     instead of self-fetching /api/v1/site-health/history.
/// </summary>
public sealed class DashboardSiteHealthBatchTests
{
    [Fact]
    public void Traffic_manifest_includes_site_health()
    {
        var manifest = new DefaultDashboardPageManifestSource().For("dashboard.traffic");
        Assert.NotNull(manifest);
        Assert.Contains("site-health", manifest!.WidgetKeys);
    }

    [Fact]
    public void PageResult_exposes_the_degradations_slice()
    {
        var snaps = (IReadOnlyList<DegradationSnapshot>)new List<DegradationSnapshot>();
        var bundle = new DashboardDatasetBundle(null, null, null, null, null, snaps);
        Assert.Same(snaps, new DashboardPageResult(bundle).Degradations);
    }

    [Fact]
    public async Task FanOut_composes_DegradationHistory_when_requested()
    {
        var store = new DegradationOnlyStore();
        var req = new DashboardBatchRequest(
            StartTime: DateTime.UtcNow.AddHours(-6),
            EndTime: DateTime.UtcNow,
            Datasets: new[] { new DatasetRequest(DatasetKind.DegradationHistory) });

        var bundle = await DashboardEventStoreBatchDefaults.FanOutAsync(store, req, default);

        Assert.Equal(1, store.DegradationCalls);
        Assert.NotNull(bundle.Degradations); // the arm populated the slice
    }

    [Fact]
    public async Task FanOut_DegradationHistory_requires_a_time_window()
    {
        var req = new DashboardBatchRequest(
            StartTime: null, EndTime: null,
            Datasets: new[] { new DatasetRequest(DatasetKind.DegradationHistory) });

        await Assert.ThrowsAsync<ArgumentException>(
            () => DashboardEventStoreBatchDefaults.FanOutAsync(new DegradationOnlyStore(), req, default));
    }

    // Minimal store: only GetDegradationHistoryAsync is exercised (the fan-out requests
    // only that kind); everything else throws to prove it's untouched. ComposeBatchAsync
    // is left as the interface default (-> FanOutAsync).
    private sealed class DegradationOnlyStore : IDashboardEventStore
    {
        public int DegradationCalls { get; private set; }

        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(
            DateTime startTime, DateTime endTime, CancellationToken ct = default)
        {
            DegradationCalls++;
            return Task.FromResult<IReadOnlyList<DegradationSnapshot>>(new List<DegradationSnapshot>());
        }

        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => throw new NotImplementedException();
    }
}
