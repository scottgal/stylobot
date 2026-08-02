using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.ViewComponents.Dashboard;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     "Endpoints always starts empty on first load" bug: when the legacy self-fetch
///     fallback (no composed pageResult, empty aggregateCache) hits a store-layer
///     decorator's cold SWR placeholder (e.g. commercial StaleWhileRevalidatingDashboardEventStore),
///     it must render the warming spinner (IsWarming=true), not the bare "no data"
///     empty state -- an empty placeholder and a genuinely-empty domain must not look
///     identical to the operator. The decorator signals this via DashboardWarmingSignal
///     stamped on HttpContext.Items; the fix wires that signal into the component's
///     fallback branch (SbEndpointsListViewComponent.cs).
/// </summary>
public sealed class SbEndpointsListWarmingSignalTests
{
    private static IOptions<StyloBotDashboardOptions> DefaultOptions() =>
        Options.Create(new StyloBotDashboardOptions { BasePath = "/stylobot" });

    private static void SetHttpContext(ViewComponent vc, HttpContext httpContext)
    {
        var viewContext = new ViewContext { HttpContext = httpContext };
        vc.ViewComponentContext = new ViewComponentContext { ViewContext = viewContext };
    }

    private static (SbEndpointsListViewComponent vc, HttpContext ctx) NewVc(FakeStore store)
    {
        // No composed pageResult and an empty aggregateCache forces the legacy
        // self-fetch fallback branch (SbEndpointsListViewComponent.cs's `else`).
        var httpContext = new DefaultHttpContext();
        // The fake store stamps the warming signal onto THIS SAME context (mirroring
        // the real decorator, which stamps the ambient HttpContext it's given via
        // IHttpContextAccessor) so the component's later IsWarming(HttpContext, ...)
        // read sees it.
        store.LastContext = httpContext;
        var vc = new SbEndpointsListViewComponent(new DashboardAggregateCache(), store, DefaultOptions());
        SetHttpContext(vc, httpContext);
        return (vc, httpContext);
    }

    [Fact]
    public async Task Cold_SWR_placeholder_renders_warming_not_bare_empty()
    {
        // Simulates a store-layer decorator (e.g. the commercial SWR wrapper) that
        // returns an empty list immediately on a cold key and stamps the signal
        // instead of blocking the request.
        var store = new FakeStore(result: [], stampWarming: true);
        var (vc, ctx) = NewVc(store);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming, "a cold SWR placeholder must render as warming, not genuinely empty");
        Assert.Empty(model.Endpoints);
    }

    [Fact]
    public async Task Warm_data_renders_normally_without_warming_flag()
    {
        var store = new FakeStore(
            result: [new DashboardEndpointStats { Method = "GET", Path = "/pricing", TotalCount = 10 }],
            stampWarming: false);
        var (vc, ctx) = NewVc(store);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.False(model.IsWarming);
        Assert.Single(model.Endpoints);
    }

    [Fact]
    public async Task Genuinely_empty_domain_without_warming_stamp_renders_as_not_warming()
    {
        // A real cold-store call that legitimately found zero endpoints (no decorator
        // in play, or the decorator's own cache already warmed to an empty result)
        // must NOT be mistaken for "still warming" -- only the explicit signal flips it.
        var store = new FakeStore(result: [], stampWarming: false);
        var (vc, ctx) = NewVc(store);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.False(model.IsWarming);
        Assert.Empty(model.Endpoints);
    }

    [Fact]
    public async Task Cold_windowed_read_also_renders_warming_not_bare_empty()
    {
        // This is the branch the LIVE page actually takes on first render: the
        // <sb-endpoints-list> tag helper always forwards range="@Model.Filters.Window",
        // so InvokeAsync's `startTime.HasValue` guard is true and it never reaches the
        // "legacy fallback" branch the other tests in this file exercise. A cold SWR
        // placeholder here must be flagged warming exactly like the unfiltered path.
        var store = new FakeStore(result: [], stampWarming: true);
        var (vc, ctx) = NewVc(store);

        var result = await vc.InvokeAsync(range: "24h") as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.True(model.IsWarming, "a cold SWR placeholder on the windowed/range-driven read must render as warming too");
        Assert.Empty(model.Endpoints);
    }

    [Fact]
    public async Task Warm_windowed_read_renders_normally_without_warming_flag()
    {
        var store = new FakeStore(
            result: [new DashboardEndpointStats { Method = "GET", Path = "/pricing", TotalCount = 10 }],
            stampWarming: false);
        var (vc, ctx) = NewVc(store);

        var result = await vc.InvokeAsync(range: "24h") as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.False(model.IsWarming);
        Assert.Single(model.Endpoints);
    }

    [Fact]
    public async Task Windowed_first_paint_reader_wins_over_cold_SWR_store_placeholder()
    {
        // A host may decorate its regular store with SWR (which correctly returns [] on
        // a cold interactive read) yet still require SSR to contain real rows. The optional
        // FOSS capability is the narrow escape hatch: it is only selected for the ordinary
        // range-driven first render, never for an audience-filtered interactive slice.
        var store = new FakeStore(result: [], stampWarming: true);
        var (vc, ctx) = NewVc(store);
        var reader = new FirstPaintReader(
            [new DashboardEndpointStats { Method = "GET", Path = "/pricing", TotalCount = 10 }]);
        DashboardEndpointsFirstPaintContext.Set(ctx, reader);

        var result = await vc.InvokeAsync(range: "24h") as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.True(reader.WasCalled);
        Assert.Equal(0, store.EndpointStatsCalls);
        Assert.False(model.IsWarming);
        Assert.Single(model.Endpoints);
    }

    private sealed class FirstPaintReader(List<DashboardEndpointStats> result) : IDashboardEndpointsFirstPaintReader
    {
        public bool WasCalled { get; private set; }

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count, DateTime? startTime, DateTime? endTime, string? audienceFilter,
            IReadOnlyList<string>? domains, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.FromResult(result);
        }
    }

    private sealed class FakeStore(List<DashboardEndpointStats> result, bool stampWarming) : IDashboardEventStore
    {
        public HttpContext? LastContext;
        public int EndpointStatsCalls { get; private set; }

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null,
            IReadOnlyList<string>? domains = null)
        {
            EndpointStatsCalls++;
            if (stampWarming) DashboardWarmingSignal.MarkWarming(LastContext, "endpoints");
            return Task.FromResult(result);
        }

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
}
