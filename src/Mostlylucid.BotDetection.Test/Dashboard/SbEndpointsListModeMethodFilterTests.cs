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
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Si2 (endpoint-IA unification): SbEndpointsList gains MODE (EndpointClassifier.
///     ClassifyMode) and re-wires the existing METHOD filter alongside the pre-existing
///     STATUS filter so the control is the ONE canonical endpoint list surface. Both
///     filters are applied AFTER the page-result/cache pull, mirroring the existing
///     path/threat/botPressure filters (SbEndpointsListViewComponent.cs), and both echo
///     back onto the model (ModeFilter/MethodFilter) so the view can render active chips
///     and round-trip the filter through URL-bound links.
/// </summary>
public sealed class SbEndpointsListModeMethodFilterTests
{
    private static IOptions<StyloBotDashboardOptions> DefaultOptions() =>
        Options.Create(new StyloBotDashboardOptions { BasePath = "/stylobot" });

    private static void SetHttpContext(ViewComponent vc, HttpContext httpContext)
    {
        var viewContext = new ViewContext { HttpContext = httpContext };
        vc.ViewComponentContext = new ViewComponentContext { ViewContext = viewContext };
    }

    private static List<DashboardEndpointStats> SampleEndpoints() =>
    [
        new() { Method = "GET", Path = "/pricing", TotalCount = 10 },       // content
        new() { Method = "GET", Path = "/api/v1/status", TotalCount = 20 }, // api
        new() { Method = "POST", Path = "/api/v1/orders", TotalCount = 5 }, // api
        new() { Method = "GET", Path = "/app.js", TotalCount = 30 },        // static
        new() { Method = "GET", Path = "/stylobot/hub", TotalCount = 1 },   // realtime
    ];

    private static (SbEndpointsListViewComponent vc, HttpContext ctx) NewVc()
    {
        var pageResult = new DashboardPageResult(new DashboardDatasetBundle(
            Summary: null, TimeBuckets: null, BotAggregate: null, Geo: null,
            Endpoints: SampleEndpoints()));
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;

        var aggregateCache = new DashboardAggregateCache();
        var vc = new SbEndpointsListViewComponent(aggregateCache, new ThrowingStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);
        return (vc, httpContext);
    }

    [Fact]
    public async Task Method_filter_keeps_only_matching_method_rows()
    {
        var (vc, _) = NewVc();

        var result = await vc.InvokeAsync(method: "POST") as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal(1, model.TotalCount);
        Assert.All(model.Endpoints, e => Assert.Equal("POST", e.Method));
        Assert.Equal("POST", model.MethodFilter);
    }

    [Fact]
    public async Task Method_filter_is_case_insensitive()
    {
        var (vc, _) = NewVc();

        var result = await vc.InvokeAsync(method: "get") as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal(4, model.TotalCount); // every GET row
    }

    [Theory]
    [InlineData("content", 1)]   // /pricing
    [InlineData("api", 2)]       // /api/v1/status + /api/v1/orders
    [InlineData("static", 1)]    // /app.js
    [InlineData("realtime", 1)]  // /stylobot/hub
    public async Task Mode_filter_keeps_only_matching_bucket(string mode, int expectedCount)
    {
        var (vc, _) = NewVc();

        var result = await vc.InvokeAsync(mode: mode) as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal(expectedCount, model.TotalCount);
        Assert.Equal(mode, model.ModeFilter);
    }

    [Fact]
    public async Task Mode_and_method_filters_compose()
    {
        var (vc, _) = NewVc();

        var result = await vc.InvokeAsync(mode: "api", method: "POST") as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal(1, model.TotalCount);
        Assert.Equal("/api/v1/orders", model.Endpoints[0].Path);
    }

    [Fact]
    public async Task Status_filter_keeps_only_matching_dominant_bucket()
    {
        var pageResult = new DashboardPageResult(new DashboardDatasetBundle(
            Summary: null, TimeBuckets: null, BotAggregate: null, Geo: null,
            Endpoints:
            [
                new() { Method = "GET", Path = "/ok", TotalCount = 10, Status2xx = 10 },
                new() { Method = "GET", Path = "/broken", TotalCount = 5, Status5xx = 5 },
            ]));
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = pageResult;
        var vc = new SbEndpointsListViewComponent(new DashboardAggregateCache(), new ThrowingStore(), DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync(status: "5xx") as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal(1, model.TotalCount);
        Assert.Equal("/broken", model.Endpoints[0].Path);
        Assert.Equal("5xx", model.StatusFilter);
    }

    [Fact]
    public async Task No_mode_filter_leaves_the_full_set_and_null_ModeFilter()
    {
        var (vc, _) = NewVc();

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal(5, model.TotalCount);
        Assert.Null(model.ModeFilter);
    }

    private sealed class ThrowingStore : IDashboardEventStore
    {
        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default) => throw new NotImplementedException();
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
}
