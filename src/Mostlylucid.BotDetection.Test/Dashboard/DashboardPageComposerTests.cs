using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

public sealed class DashboardPageComposerTests
{
    [DashboardWidget("sum", DatasetKind.SummaryStats)]
    private sealed class SummaryW { }

    [DashboardWidget("bots", DatasetKind.BotAggregate)]
    private sealed class BotsW { }

    [DashboardWidget("sum-dup", DatasetKind.SummaryStats)]
    private sealed class SummaryWDup { }

    [Fact]
    public async Task Composer_requests_only_manifest_kinds()
    {
        var catalog = DashboardWidgetCatalog.BuildFrom(new[] { typeof(SummaryW), typeof(BotsW) });
        var store = new RecordingStore();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifest = new DashboardPageManifest("dashboard.traffic", new[] { "sum", "bots" });

        var result = await composer.ComposeAsync(manifest,
            new DashboardPageWindow(null, null, null, null, null, TopN: 50, BucketMinutes: 60), default);

        Assert.Equal(
            new[] { DatasetKind.SummaryStats, DatasetKind.BotAggregate },
            store.LastRequest!.Datasets.Select(d => d.Kind).OrderBy(k => k).ToArray());
        Assert.NotNull(result.Summary);
    }

    [Fact]
    public async Task Composer_deduplicates_kinds_when_two_widgets_need_same_dataset()
    {
        // Two different widget keys map to the same DatasetKind — the request should contain it once.
        var catalog = DashboardWidgetCatalog.BuildFrom(new[] { typeof(SummaryW), typeof(SummaryWDup) });
        var store = new RecordingStore();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifest = new DashboardPageManifest("dashboard.traffic", new[] { "sum", "sum-dup" });

        _ = await composer.ComposeAsync(manifest,
            new DashboardPageWindow(null, null, null, null, null, TopN: 50, BucketMinutes: 60), default);

        Assert.Single(store.LastRequest!.Datasets);
        Assert.Equal(DatasetKind.SummaryStats, store.LastRequest.Datasets[0].Kind);
    }

    [Fact]
    public async Task DefaultDashboardPageManifestSource_returns_null_for_unknown_key()
    {
        var source = new DefaultDashboardPageManifestSource();
        Assert.Null(source.For("nonexistent.page"));
    }

    private sealed class RecordingStore : IDashboardEventStore
    {
        public DashboardBatchRequest? LastRequest { get; private set; }

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
        {
            LastRequest = request;
            var bundle = new DashboardDatasetBundle(
                Summary: new DashboardSummary
                {
                    Timestamp = DateTime.UtcNow,
                    TotalRequests = 0,
                    BotRequests = 0,
                    HumanRequests = 0,
                    UncertainRequests = 0,
                    RiskBandCounts = new Dictionary<string, int>(),
                    TopBotTypes = new Dictionary<string, int>(),
                    TopActions = new Dictionary<string, int>(),
                    UniqueSignatures = 0
                },
                TimeBuckets: null,
                BotAggregate: new List<DashboardTopBotEntry>(),
                Geo: null,
                Endpoints: null);
            return Task.FromResult(bundle);
        }

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
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
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
    }
}