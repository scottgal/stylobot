using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

public class DashboardBatchDefaultsTests
{
    [Fact]
    public async Task FanOut_requests_only_the_kinds_asked_for()
    {
        // Default interface methods are only accessible through the interface reference.
        var fake = new FakeStore();
        IDashboardEventStore store = fake;
        var req = new DashboardBatchRequest(
            StartTime: null, EndTime: null,
            Datasets: [new DatasetRequest(DatasetKind.SummaryStats), new DatasetRequest(DatasetKind.BotAggregate, TopN: 10)]);

        var bundle = await store.ComposeBatchAsync(req, default);

        Assert.NotNull(bundle.Summary);
        Assert.NotNull(bundle.BotAggregate);
        Assert.Equal(10, fake.LastTopBotsCount);   // TopN forwarded
        Assert.Null(bundle.Geo);                    // not requested -> not fetched
        Assert.False(fake.GeoCalled);
    }

    [Fact]
    public async Task TimeBuckets_throws_when_no_time_window()
    {
        var fake = new FakeStore();
        IDashboardEventStore store = fake;
        var req = new DashboardBatchRequest(
            StartTime: null, EndTime: null,
            Datasets: [new DatasetRequest(DatasetKind.TimeBuckets)]);

        var ex = await Assert.ThrowsAsync<ArgumentException>(() => store.ComposeBatchAsync(req, default));
        Assert.Contains("TimeBuckets", ex.Message);
        Assert.Contains("StartTime", ex.Message);
    }

    private sealed class FakeStore : IDashboardEventStore
    {
        public bool GeoCalled;
        public int LastTopBotsCount;

        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = default,
                TotalRequests = 0,
                BotRequests = 0,
                HumanRequests = 0,
                UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0
            });

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            LastTopBotsCount = count;
            return Task.FromResult(new List<DashboardTopBotEntry>());
        }

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            GeoCalled = true;
            return Task.FromResult(new List<DashboardCountryStats>());
        }

        // Remaining members — not exercised by this test
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
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
    }
}