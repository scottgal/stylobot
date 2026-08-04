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
///     Internal-filter posture (foss-internal-filter-posture): the endpoint control's
///     "Show self-probe" toggle threads through as the "all_incl_internal" audience-filter
///     token -- a distinct value from "internal" (Internal-ONLY) meaning "everything,
///     Internal included, no bot/human gate". Two things must hold for the toggle to
///     actually work when this VC is rendered on a composer-driven page (the page composer
///     stashes a DashboardPageResult on HttpContext.Items BEFORE the VC runs):
///     <list type="bullet">
///         <item>An explicit non-empty audience (like "all_incl_internal") must always win
///         over the composed pageResult snapshot -- the composed bundle is built with a
///         fixed, page-level audienceFilter (see SbWidgetBatchMiddleware.BuildBatchWindow,
///         which only ever composes "bots"/"humans"/null) that can never satisfy an
///         "include Internal" request, so it must never shadow the toggle.</item>
///         <item>With no audience override, the existing composed/cache path is unchanged
///         (zero store calls) -- this pins the pre-existing fast path so the bypass-order
///         fix above doesn't regress it.</item>
///     </list>
/// </summary>
public sealed class SbEndpointsListShowInternalToggleTests
{
    private static IOptions<StyloBotDashboardOptions> DefaultOptions() =>
        Options.Create(new StyloBotDashboardOptions { BasePath = "/stylobot" });

    private static void SetHttpContext(ViewComponent vc, HttpContext httpContext)
    {
        var viewContext = new ViewContext { HttpContext = httpContext };
        vc.ViewComponentContext = new ViewComponentContext { ViewContext = viewContext };
    }

    private static DashboardPageResult ComposedPageResultExcludingInternal() => new(new DashboardDatasetBundle(
        Summary: null, TimeBuckets: null, BotAggregate: null, Geo: null,
        Endpoints:
        [
            new() { Method = "GET", Path = "/widget", TotalCount = 10 }, // real traffic only --
            // the composed bundle was built with Internal already excluded (the new default),
            // so it can never contain /metrics-style self-probe rows for the toggle to reveal.
        ]));

    [Fact]
    public async Task Show_self_probe_toggle_bypasses_the_composed_pageResult_and_hits_the_store()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = ComposedPageResultExcludingInternal();
        var store = new RecordingStore
        {
            EndpointsToReturn =
            [
                new() { Method = "GET", Path = "/widget", TotalCount = 10 },
                new() { Method = "GET", Path = "/metrics", TotalCount = 4 }, // Internal-only in the real data; only visible via the store path once the toggle is on
            ]
        };
        var vc = new SbEndpointsListViewComponent(new DashboardAggregateCache(), store, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync(audience: "all_incl_internal") as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal("all_incl_internal", store.LastAudienceFilterRequested);
        Assert.Equal(2, model.TotalCount);
        Assert.Contains(model.Endpoints, e => e.Path == "/metrics");
        Assert.Equal("all_incl_internal", model.AudienceFilter);
    }

    [Fact]
    public async Task No_audience_override_still_uses_the_composed_pageResult_with_zero_store_calls()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["sb.dashboard.pageresult"] = ComposedPageResultExcludingInternal();
        // ThrowingStore-equivalent: any store call fails the test outright.
        var store = new RecordingStore { ThrowOnCall = true };
        var vc = new SbEndpointsListViewComponent(new DashboardAggregateCache(), store, DefaultOptions());
        SetHttpContext(vc, httpContext);

        var result = await vc.InvokeAsync() as ViewViewComponentResult;

        var model = Assert.IsType<EndpointsListModel>(result!.ViewData!.Model);
        Assert.Equal(1, model.TotalCount); // the composed snapshot, unfiltered
        Assert.DoesNotContain(model.Endpoints, e => e.Path == "/metrics");
    }

    /// <summary>Records the audienceFilter passed to GetEndpointStatsAsync so the bypass can be
    ///     asserted directly, rather than inferred from row content alone.</summary>
    private sealed class RecordingStore : IDashboardEventStore
    {
        public List<DashboardEndpointStats> EndpointsToReturn { get; set; } = [];
        public string? LastAudienceFilterRequested { get; private set; }
        public bool ThrowOnCall { get; set; }

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            if (ThrowOnCall) throw new InvalidOperationException("Store must not be called when a composed pageResult is available and no audience override is active.");
            LastAudienceFilterRequested = audienceFilter;
            return Task.FromResult(EndpointsToReturn);
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
