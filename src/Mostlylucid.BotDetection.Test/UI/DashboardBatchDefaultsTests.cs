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

    [Fact]
    public async Task FanOut_issues_the_reads_concurrently_and_populates_the_bundle()
    {
        // Three independent slices requested; each Get*Async blocks on a shared
        // gate until every expected read has entered, then all release together.
        // If the fan-out were serial, the first read would never see the others
        // enter, the gate would time out, and observed concurrency would stay 1.
        var start = new DateTime(2026, 07, 25, 0, 0, 0, DateTimeKind.Utc);
        var store = new ConcurrencyRecordingStore(expectedConcurrent: 3);
        IDashboardEventStore s = store;
        var req = new DashboardBatchRequest(
            StartTime: start, EndTime: start.AddHours(6),
            Datasets:
            [
                new DatasetRequest(DatasetKind.SummaryStats),
                new DatasetRequest(DatasetKind.BotAggregate, TopN: 5),
                new DatasetRequest(DatasetKind.GeoBreakdown, TopN: 7)
            ]);

        var bundle = await s.ComposeBatchAsync(req, default);

        // Proof the reads overlapped: max simultaneously-in-flight reads > 1.
        Assert.Equal(3, store.MaxObservedConcurrency);
        Assert.False(store.GateTimedOut, "reads did not overlap (gate timed out -> serial execution)");

        // Bundle correctly assembled from the concurrent results.
        Assert.NotNull(bundle.Summary);
        Assert.Equal(4242, bundle.Summary!.TotalRequests);   // distinctive sentinel
        Assert.NotNull(bundle.BotAggregate);
        Assert.Single(bundle.BotAggregate!);
        Assert.NotNull(bundle.Geo);
        Assert.Single(bundle.Geo!);
        Assert.Null(bundle.TimeBuckets);   // not requested
        Assert.Null(bundle.Endpoints);     // not requested
    }

    /// <summary>
    ///     Records the peak number of Get*Async calls executing simultaneously.
    ///     Each read enters, bumps the live count, and waits on a barrier that only
    ///     releases once <c>expectedConcurrent</c> reads are in flight (or a timeout
    ///     fires). Serial fan-out can never satisfy the barrier -> timeout + peak 1.
    /// </summary>
    private sealed class ConcurrencyRecordingStore : IDashboardEventStore
    {
        private readonly int _expected;
        private readonly TaskCompletionSource _allEntered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _live;
        private int _peak;

        public ConcurrencyRecordingStore(int expectedConcurrent) => _expected = expectedConcurrent;

        public int MaxObservedConcurrency => Volatile.Read(ref _peak);
        public bool GateTimedOut { get; private set; }

        private async Task EnterGateAsync()
        {
            var live = Interlocked.Increment(ref _live);
            // Track the running maximum.
            int observed;
            do { observed = Volatile.Read(ref _peak); }
            while (live > observed && Interlocked.CompareExchange(ref _peak, live, observed) != observed);

            if (live >= _expected) _allEntered.TrySetResult();

            var completed = await Task.WhenAny(_allEntered.Task, Task.Delay(TimeSpan.FromSeconds(5)));
            if (completed != _allEntered.Task) GateTimedOut = true;

            Interlocked.Decrement(ref _live);
        }

        public async Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            await EnterGateAsync();
            return new DashboardSummary
            {
                Timestamp = default,
                TotalRequests = 4242,
                BotRequests = 0,
                HumanRequests = 0,
                UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0
            };
        }

        public async Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            await EnterGateAsync();
            return [new DashboardTopBotEntry { PrimarySignature = "sig-1" }];
        }

        public async Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            await EnterGateAsync();
            return [new DashboardCountryStats { CountryCode = "US" }];
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
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
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
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
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