using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Domains;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Task 4 TDD: asserts that each traffic ViewComponent reads its slice from a
///     <see cref="DashboardPageResult"/> stashed in <c>HttpContext.Items</c> and does NOT
///     call <c>IDashboardEventStore</c> when the result is present.
///
///     Uses a throwing store so any self-fetch attempt surfaces immediately as an exception.
/// </summary>
public sealed class ViewComponentFromResultTests
{
    // ---------- helpers ----------

    private static IOptions<StyloBotDashboardOptions> DefaultOptions() =>
        Options.Create(new StyloBotDashboardOptions { BasePath = "/stylobot" });

    private static DashboardSummary EmptySummary() => new()
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
    };

    private static DashboardPageResult MakeResult(
        DashboardSummary? summary = null,
        IReadOnlyList<DashboardTopBotEntry>? botAggregate = null,
        IReadOnlyList<DashboardCountryStats>? geo = null,
        IReadOnlyList<DashboardEndpointStats>? endpoints = null,
        IReadOnlyList<SessionListEntry>? sessionsRaw = null,
        int? sessionsRawTotalCount = null,
        IReadOnlyList<ThreatEntry>? threatsRaw = null,
        IReadOnlyList<DashboardUserAgentSummary>? userAgentsRaw = null)
    {
        var bundle = new DashboardDatasetBundle(
            Summary: summary,
            TimeBuckets: null,
            BotAggregate: botAggregate,
            Geo: geo,
            Endpoints: endpoints);
        return new DashboardPageResult(
            bundle,
            sessionsRaw: sessionsRaw,
            sessionsRawTotalCount: sessionsRawTotalCount,
            threatsRaw: threatsRaw,
            userAgentsRaw: userAgentsRaw);
    }

    /// <summary>
    ///     Every method throws. If a VC calls the session archive when a composed page
    ///     result already supplies the Sessions slice, the test fails.
    /// </summary>
    private sealed class ThrowingDetectionArchive : IDetectionArchive
    {
        private static InvalidOperationException Fail() =>
            new("IDetectionArchive must not be called when DashboardPageResult supplies the composed slice");

        public string? PersistenceConnectionString => null;
        public Task<long> AddSessionAsync(RequestScope scope, PersistedSession session, CancellationToken ct = default) => throw Fail();
        public Task<long> AddEchoAsync(Mostlylucid.BotDetection.Orchestration.Sessions.SessionEcho echo, CancellationToken ct = default) => throw Fail();
        public Task UpsertSignatureAsync(RequestScope scope, PersistedSignature signature, CancellationToken ct = default) => throw Fail();
        public Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default) => throw Fail();
        public Task AddRequestAsync(RequestScope scope, PersistedRequest request, CancellationToken ct = default) => throw Fail();
        public Task AddRequestBatchAsync(RequestScope scope, IReadOnlyList<PersistedRequest> requests, CancellationToken ct = default) => throw Fail();
        public Task<List<PersistedRequest>> GetUnatomizedRequestsAsync(int limit = 5000, CancellationToken ct = default) => throw Fail();
        public Task<List<PersistedRequest>> GetRecentRequestsAsync(int limit = 5000, DateTime? sinceUtc = null, CancellationToken ct = default) => throw Fail();
        public Task LinkRequestsToSessionAsync(long sessionId, IReadOnlyList<long> requestIds, CancellationToken ct = default) => throw Fail();
        public Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default) => throw Fail();
        public Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, DateTime? since = null, CancellationToken ct = default) => throw Fail();
        public Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default) => throw Fail();
        public Task<string> ResolveSignatureAsync(string requestedSignatureId, CancellationToken ct = default) => throw Fail();
        public Task RecordSignatureMergeAsync(string oldSignatureId, string newSignatureId, string reason, CancellationToken ct = default) => throw Fail();
        public Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default) => throw Fail();
        public Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default) => throw Fail();
        public Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default) => throw Fail();
        public Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default) => throw Fail();
        public Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default) => throw Fail();
        public Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default) => throw Fail();
        public Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default) => throw Fail();
        public Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default) => throw Fail();
        public Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default) => throw Fail();
        public Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default) => throw Fail();
        public Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default) => throw Fail();
        public Task PruneAsync(TimeSpan retention, CancellationToken ct = default) => throw Fail();
        public Task PruneBucketsAsync(TimeSpan retention, CancellationToken ct = default) => throw Fail();
        public Task<List<(string Signature, int SessionCount)>> GetOverflowingSignaturesAsync(int maxPerSignature, int limit = 500, CancellationToken ct = default) => throw Fail();
        public Task<CompactionResult> CompactSignatureSessionsAsync(string signature, int keepCount, CancellationToken ct = default) => throw Fail();
        public Task<List<CompactionSignatureInfo>> GetSignaturePriorityInfoAsync(List<string> signatures, CancellationToken ct = default) => throw Fail();
        public Task<List<string>> GetActiveEntityIdsAsync(DateTime cutoff, int limit = 100, CancellationToken ct = default) => throw Fail();
        public Task InitializeAsync(CancellationToken ct = default) => throw Fail();
    }

    /// <summary>
    ///     Sets ViewComponentContext.ViewContext.HttpContext so <c>ViewComponent.HttpContext</c>
    ///     returns the provided context.
    /// </summary>
    private static void SetHttpContext(ViewComponent vc, HttpContext httpContext)
    {
        var viewContext = new ViewContext { HttpContext = httpContext };
        vc.ViewComponentContext = new ViewComponentContext
        {
            ViewContext = viewContext,
        };
    }

    // ---------- throwing store ----------

    /// <summary>
    ///     Every method throws. If a VC calls the store when a page result is present, the test fails.
    /// </summary>
    private sealed class ThrowingEventStore : IDashboardEventStore
    {
        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default) =>
            throw new InvalidOperationException("Store must not be called when DashboardPageResult is in HttpContext.Items");
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new InvalidOperationException("Store must not be called when DashboardPageResult is in HttpContext.Items");
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new InvalidOperationException("Store must not be called when DashboardPageResult is in HttpContext.Items");
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new InvalidOperationException("Store must not be called when DashboardPageResult is in HttpContext.Items");
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new InvalidOperationException("Store must not be called when DashboardPageResult is in HttpContext.Items");
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new InvalidOperationException("Store must not be called when DashboardPageResult is in HttpContext.Items");
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
    }

    // ---------- SbTopBots ----------

    [Fact]
    public async Task TopBots_reads_from_page_result_without_calling_store()
    {
        // Arrange: known bot list in the page result
        var knownBots = new List<DashboardTopBotEntry>
        {
            new() { PrimarySignature = "abc123", HitCount = 42, IsKnownBot = true, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow },
            new() { PrimarySignature = "def456", HitCount = 7,  IsKnownBot = false, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow },
        };
        var pageResult = MakeResult(botAggregate: knownBots);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var vc = new SbTopBotsViewComponent(new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        // Act — throwing store means any self-fetch → exception
        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        // Assert
        Assert.NotNull(result);
        var model = Assert.IsType<TopBotsListModel>(result!.ViewData!.Model);
        // The two entries should be reflected in Counts.All (both non-internal)
        Assert.Equal(2, model.Counts.All);
    }

    [Fact]
    public async Task TopBots_falls_back_to_store_when_no_page_result()
    {
        // Arrange: no DashboardPageResult in Items → VC should self-fetch
        var entries = new List<DashboardTopBotEntry>
        {
            new() { PrimarySignature = "xyz", HitCount = 1, IsKnownBot = true, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow }
        };
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetTopBotsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
             .ReturnsAsync(entries);

        var httpContext = new DefaultHttpContext();
        // No Items["sb.dashboard.pageresult"]

        var vc = new SbTopBotsViewComponent(store.Object, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        store.Verify(s => s.GetTopBotsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
    }

    // ---------- SbSummaryStats ----------

    [Fact]
    public async Task SummaryStats_reads_from_page_result_without_calling_store()
    {
        var summary = EmptySummary();
        var pageResult = MakeResult(summary: summary);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var vc = new SbSummaryStatsViewComponent(new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<SummaryStatsModel>(result!.ViewData!.Model);
        Assert.Same(summary, model.Summary);
    }

    [Fact]
    public async Task SummaryStats_falls_back_to_store_when_no_page_result()
    {
        var summary = EmptySummary();
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetSummaryAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
             .ReturnsAsync(summary);

        var httpContext = new DefaultHttpContext();

        var vc = new SbSummaryStatsViewComponent(store.Object, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        store.Verify(s => s.GetSummaryAsync(It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
    }

    // ---------- SbCountriesList ----------

    [Fact]
    public async Task Countries_reads_from_page_result_without_calling_store()
    {
        var countries = new List<DashboardCountryStats>
        {
            new() { CountryCode = "US", TotalCount = 100, BotCount = 10 },
            new() { CountryCode = "DE", TotalCount = 50,  BotCount = 5  },
        };
        var pageResult = MakeResult(geo: countries);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbCountriesListViewComponent(aggregateCache, new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<CountriesListModel>(result!.ViewData!.Model);
        Assert.Equal(2, model.TotalCount);
    }

    [Fact]
    public async Task Countries_falls_back_to_store_when_no_page_result()
    {
        var countries = new List<DashboardCountryStats>
        {
            new() { CountryCode = "FR", TotalCount = 20, BotCount = 2 }
        };
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetCountryStatsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
             .ReturnsAsync(countries);

        var httpContext = new DefaultHttpContext();

        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbCountriesListViewComponent(aggregateCache, store.Object, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        store.Verify(s => s.GetCountryStatsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
    }

    // ---------- SbEndpointsList ----------

    [Fact]
    public async Task Endpoints_reads_from_page_result_without_calling_store()
    {
        var endpoints = new List<DashboardEndpointStats>
        {
            new() { Method = "GET", Path = "/api/test", TotalCount = 200, BotCount = 20 },
        };
        var pageResult = MakeResult(endpoints: endpoints);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbEndpointsListViewComponent(aggregateCache, new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal(1, model.TotalCount);
    }

    [Fact]
    public async Task Endpoints_falls_back_to_store_when_no_page_result()
    {
        var endpoints = new List<DashboardEndpointStats>
        {
            new() { Method = "POST", Path = "/api/data", TotalCount = 50, BotCount = 5 }
        };
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetEndpointStatsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
             .ReturnsAsync(endpoints);

        var httpContext = new DefaultHttpContext();

        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbEndpointsListViewComponent(aggregateCache, store.Object, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        store.Verify(s => s.GetEndpointStatsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
    }

    // ---------- SbSessionsList (window-threading Task 1) ----------

    [Fact]
    public async Task Sessions_reads_from_page_result_without_calling_store()
    {
        var entries = new List<SessionListEntry>
        {
            new() { Id = 1, Signature = "sig-a", StartedAt = DateTime.UtcNow.AddMinutes(-10), EndedAt = DateTime.UtcNow, RequestCount = 3, DominantState = "PageView", IsBot = true, AvgBotProbability = 0.9, RiskBand = "High" },
            new() { Id = 2, Signature = "sig-b", StartedAt = DateTime.UtcNow.AddMinutes(-5), EndedAt = DateTime.UtcNow, RequestCount = 1, DominantState = "PageView", IsBot = false, AvgBotProbability = 0.1, RiskBand = "Low" },
        };
        var pageResult = MakeResult(sessionsRaw: entries, sessionsRawTotalCount: entries.Count);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var vc = new SbSessionsListViewComponent(new ThrowingDetectionArchive(), new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<SessionsListModel>(result!.ViewData!.Model);
        Assert.Equal(2, model.TotalCount);
    }

    [Fact]
    public async Task Sessions_falls_back_to_store_when_no_page_result()
    {
        var archive = new Mock<IDetectionArchive>();
        archive.Setup(a => a.GetRecentSessionsAsync(It.IsAny<int>(), It.IsAny<bool?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new List<PersistedSession>());
        var eventStore = new Mock<IDashboardEventStore>();
        eventStore.Setup(s => s.GetSignaturesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool?>()))
                   .ReturnsAsync(new List<DashboardSignatureEvent>());
        eventStore.Setup(s => s.GetTopBotsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
                   .ReturnsAsync(new List<DashboardTopBotEntry>());

        var httpContext = new DefaultHttpContext();

        var vc = new SbSessionsListViewComponent(archive.Object, eventStore.Object, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        archive.Verify(a => a.GetRecentSessionsAsync(It.IsAny<int>(), It.IsAny<bool?>(), It.IsAny<DateTime?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Sessions_scoped_to_primary_signature_ignores_page_result()
    {
        // A visitor-detail "Hit history" embed always self-fetches -- the composed
        // Sessions slice is the UNSCOPED global timeline and does not answer a
        // per-signature query.
        var entries = new List<SessionListEntry>
        {
            new() { Id = 1, Signature = "sig-a", StartedAt = DateTime.UtcNow.AddMinutes(-10), EndedAt = DateTime.UtcNow, RequestCount = 3, DominantState = "PageView", IsBot = true, AvgBotProbability = 0.9, RiskBand = "High" },
        };
        var pageResult = MakeResult(sessionsRaw: entries, sessionsRawTotalCount: entries.Count);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var archive = new Mock<IDetectionArchive>();
        archive.Setup(a => a.GetSessionsAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
               .ReturnsAsync(new List<PersistedSession>());
        var eventStore = new Mock<IDashboardEventStore>();
        eventStore.Setup(s => s.GetSignaturesAsync(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<bool?>()))
                   .ReturnsAsync(new List<DashboardSignatureEvent>());
        eventStore.Setup(s => s.GetTopBotsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
                   .ReturnsAsync(new List<DashboardTopBotEntry>());

        var vc = new SbSessionsListViewComponent(archive.Object, eventStore.Object, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync(primarySignature: "sig-scoped") as ViewViewComponentResult;

        Assert.NotNull(result);
        archive.Verify(a => a.GetSessionsAsync("sig-scoped", It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // ---------- SbThreatsList (window-threading Task 1) ----------

    [Fact]
    public async Task Threats_reads_from_page_result_without_calling_store()
    {
        var threats = new List<ThreatEntry>
        {
            new() { Signature = "sig-a", Path = "/wp-admin", ThreatScore = 0.8, Timestamp = DateTime.UtcNow },
        };
        var pageResult = MakeResult(threatsRaw: threats);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var vc = new SbThreatsListViewComponent(new ThrowingEventStore(), new StyloBotDashboardOptions { BasePath = "/stylobot" });
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<ThreatsListModel>(result!.ViewData!.Model);
        Assert.Equal(1, model.TotalCount);
    }

    [Fact]
    public async Task Threats_falls_back_to_store_when_no_page_result()
    {
        var threats = new List<ThreatEntry>
        {
            new() { Signature = "sig-b", Path = "/xmlrpc.php", ThreatScore = 0.5, Timestamp = DateTime.UtcNow },
        };
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetThreatsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()))
             .ReturnsAsync(threats);

        var httpContext = new DefaultHttpContext();

        var vc = new SbThreatsListViewComponent(store.Object, new StyloBotDashboardOptions { BasePath = "/stylobot" });
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        store.Verify(s => s.GetThreatsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>()), Times.Once);
    }

    // ---------- SbUserAgentsList (window-threading Task 1) ----------

    [Fact]
    public async Task UserAgents_reads_from_page_result_without_calling_store()
    {
        var userAgents = new List<DashboardUserAgentSummary>
        {
            new()
            {
                Family = "Chrome", Category = "browser", TotalCount = 40, BotCount = 4, HumanCount = 36,
                BotRate = 0.1, Versions = new Dictionary<string, int>(), Countries = new Dictionary<string, int>(),
                AvgConfidence = 0.8, AvgProcessingTimeMs = 5.0, LastSeen = DateTime.UtcNow,
            },
        };
        var pageResult = MakeResult(userAgentsRaw: userAgents);

        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var throwingStore = new ThrowingEventStore();
        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbUserAgentsListViewComponent(aggregateCache, new DashboardUserAgentAggregator(throwingStore), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<UserAgentsListModel>(result!.ViewData!.Model);
        Assert.Equal(1, model.TotalCount);
    }

    [Fact]
    public async Task UserAgents_falls_back_to_store_when_no_page_result()
    {
        var userAgents = new List<DashboardUserAgentSummary>
        {
            new()
            {
                Family = "Firefox", Category = "browser", TotalCount = 12, BotCount = 1, HumanCount = 11,
                BotRate = 0.08, Versions = new Dictionary<string, int>(), Countries = new Dictionary<string, int>(),
                AvgConfidence = 0.75, AvgProcessingTimeMs = 4.0, LastSeen = DateTime.UtcNow,
            },
        };
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetDetectionsAsync(It.IsAny<DashboardFilter?>(), It.IsAny<CancellationToken>()))
             .ReturnsAsync(new List<DashboardDetectionEvent>());

        var httpContext = new DefaultHttpContext();

        // Update() (not a mutation of Current.UserAgents) -- AggregateSnapshot.Empty is a
        // shared static singleton until the first Update(); mutating its list in place
        // would leak into every other test that constructs a fresh DashboardAggregateCache.
        var aggregateCache = new DashboardAggregateCache();
        aggregateCache.Update(new DashboardAggregateCache.AggregateSnapshot
        {
            Countries = [],
            Endpoints = [],
            UserAgents = userAgents,
        });
        var vc = new SbUserAgentsListViewComponent(aggregateCache, new DashboardUserAgentAggregator(store.Object), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<UserAgentsListModel>(result!.ViewData!.Model);
        Assert.Equal(1, model.TotalCount);
    }

    // ---------- Task 2: Warming placeholder -- genuine cold miss never self-fetches ----------
    //
    // DashboardPageResult.Warming is the cold-miss placeholder: no snapshot has EVER been
    // composed for this envelope. Every in-scope VC must render its model's own IsWarming=true
    // + empty data instead of falling through to a live store call. Every store/archive fake
    // below throws on first use, so a regression back to "self-fetch on Warming" surfaces as an
    // exception, not a silently-slow request.

    [Fact]
    public async Task SummaryStats_warming_pageresult_renders_placeholder_without_calling_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var vc = new SbSummaryStatsViewComponent(new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<SummaryStatsModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
    }

    [Fact]
    public async Task TopBots_warming_pageresult_renders_placeholder_without_calling_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var vc = new SbTopBotsViewComponent(new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<TopBotsListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
        Assert.Empty(model.Bots);
    }

    [Fact]
    public async Task Countries_warming_pageresult_renders_placeholder_without_calling_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbCountriesListViewComponent(aggregateCache, new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<CountriesListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
        Assert.Empty(model.Countries);
    }

    [Fact]
    public async Task Endpoints_warming_pageresult_renders_placeholder_without_calling_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbEndpointsListViewComponent(aggregateCache, new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
        Assert.Empty(model.Endpoints);
    }

    [Fact]
    public async Task Sessions_warming_pageresult_renders_placeholder_without_calling_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var vc = new SbSessionsListViewComponent(new ThrowingDetectionArchive(), new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<SessionsListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
        Assert.Empty(model.Sessions);
    }

    [Fact]
    public async Task Sessions_warming_pageresult_scoped_embed_also_renders_placeholder()
    {
        // Even the primarySignature-scoped "Hit history" embed must honour a genuinely
        // Warming pageResult -- it is a different concern from "composed slice doesn't
        // cover this scope" (which DOES still self-fetch, per the earlier Task 1 test).
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var vc = new SbSessionsListViewComponent(new ThrowingDetectionArchive(), new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync(primarySignature: "sig-scoped") as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<SessionsListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
    }

    [Fact]
    public async Task Threats_warming_pageresult_renders_placeholder_without_calling_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var vc = new SbThreatsListViewComponent(new ThrowingEventStore(), new StyloBotDashboardOptions { BasePath = "/stylobot" });
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<ThreatsListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
        Assert.Empty(model.Threats);
    }

    [Fact]
    public async Task UserAgents_warming_pageresult_renders_placeholder_without_calling_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var throwingStore = new ThrowingEventStore();
        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbUserAgentsListViewComponent(aggregateCache, new DashboardUserAgentAggregator(throwingStore), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<UserAgentsListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
        Assert.Empty(model.UserAgents);
    }

    [Fact]
    public async Task Visitors_warming_pageresult_renders_placeholder_without_calling_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = DashboardPageResult.Warming;

        var vc = new SbVisitorListViewComponent(new ThrowingEventStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        var model = Assert.IsType<VisitorListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming);
        Assert.Empty(model.Visitors);
    }

    // ---------- Warming vs. "composer ran but didn't request this slice" (unchanged) ----------

    [Fact]
    public async Task TopBots_non_warming_pageresult_with_null_slice_still_falls_back_to_store()
    {
        // pageResult present, NOT warming, but BotAggregate is null (composer ran, this
        // page's manifest didn't request BotAggregate) -- behavior must be UNCHANGED: self-fetch.
        var entries = new List<DashboardTopBotEntry>
        {
            new() { PrimarySignature = "xyz", HitCount = 1, IsKnownBot = true, FirstSeen = DateTime.UtcNow, LastSeen = DateTime.UtcNow }
        };
        var store = new Mock<IDashboardEventStore>();
        store.Setup(s => s.GetTopBotsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()))
             .ReturnsAsync(entries);

        var pageResult = MakeResult(); // no BotAggregate, IsWarming defaults to false
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var vc = new SbTopBotsViewComponent(store.Object, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        Assert.NotNull(result);
        store.Verify(s => s.GetTopBotsAsync(It.IsAny<int>(), It.IsAny<DateTime?>(), It.IsAny<DateTime?>(), It.IsAny<string?>(), It.IsAny<IReadOnlyList<string>?>()), Times.Once);
    }
}