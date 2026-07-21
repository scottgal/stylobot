using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Options;
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
        IReadOnlyList<DashboardEndpointStats>? endpoints = null)
    {
        var bundle = new DashboardDatasetBundle(
            Summary: summary,
            TimeBuckets: null,
            BotAggregate: botAggregate,
            Geo: geo,
            Endpoints: endpoints);
        return new DashboardPageResult(bundle);
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
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
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
}