using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Test.Helpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Task 3 TDD: asserts that the traffic page issues exactly ONE <c>ComposeBatchAsync</c>
///     for the current window's five datasets, and that none of the per-widget
///     <c>Get*Async</c> methods are invoked for the current window by the controller
///     (they may still be called by the composer internally — that is the point).
/// </summary>
public sealed class TrafficControllerComposeTests
{
    // ---------- helpers ----------

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

    private static IOptions<DashboardLayoutOptions> DefaultLayoutOptions() =>
        Options.Create(new DashboardLayoutOptions
        {
            DefaultTimeWindowMinutes = 360,
            TrafficCardTopN = 10,
            TopEndpointsMinSamplesForPerf = 5,
        });

    private static IOptions<ThreatsOptions> DefaultThreatsOptions() =>
        Options.Create(new ThreatsOptions());

    // The controller now reads through the content cache (Task 3). The cache's
    // factory is the composer, so a cold miss still issues exactly one
    // ComposeBatchAsync — the "one compose per current window" contract holds,
    // and repeated reads at the same tick are served from the cache. Structural §8
    // fix: GetCurrentAsync never composes on the request thread anymore, so this
    // test-only decorator simulates "the materializer already warmed it" so the
    // controller's own batching behavior can still be asserted in isolation.
    private static IDashboardContentCache ContentCache(DefaultDashboardPageComposer composer, long tick = 1) =>
        new Mostlylucid.BotDetection.Test.Helpers.AutoWarmingContentCache(
            new DashboardContentCache((m, w, ct) => composer.ComposeAsync(m, w, ct), () => tick,
                Options.Create(new DashboardMaterializerOptions())),
            () => tick);

    // ---------- recording store ----------

    /// <summary>
    ///     Records all method calls. ComposeBatchAsync succeeds; all individual
    ///     Get*Async methods throw so any direct call from the controller is
    ///     immediately visible as an exception.
    /// </summary>
    private sealed class RecordingEventStore : IDashboardEventStore
    {
        public int ComposeBatchCallCount { get; private set; }
        public DashboardBatchRequest? LastBatchRequest { get; private set; }
        public List<DashboardBatchRequest> BatchRequests { get; } = new();

        // Null by default matches "TimeBuckets branch hasn't been exercised" -- set to
        // a non-null list to opt a test into the normal (already-warm) shape.
        public List<DashboardTimeSeriesPoint>? TimeBucketsOverride { get; set; } = new();

        // Live-tier overlay slices (folded into the compose bundle 2026-08-22 per the
        // render-once ruling). Set to a non-null list to prove the controller MERGES the
        // LIVE values over the base -- the merge is the fix under test.
        public List<DashboardTimeSeriesPoint>? LiveTimeBucketsOverride { get; set; } = new();
        public List<DashboardEndpointStats>? LiveEndpointStatsOverride { get; set; } = new();

        // Prior-window calls are the two allowed per-widget calls that the
        // controller still issues directly. Track them so tests can assert
        // how many times each was called.
        public int GetTopBotsCallCount { get; private set; }
        public int GetSummaryCallCount { get; private set; }

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
        {
            ComposeBatchCallCount++;
            LastBatchRequest = request;
            BatchRequests.Add(request);
            var bundle = new DashboardDatasetBundle(
                Summary: EmptySummary(),
                TimeBuckets: TimeBucketsOverride,
                BotAggregate: new List<DashboardTopBotEntry>(),
                Geo: new List<DashboardCountryStats>(),
                Endpoints: new List<DashboardEndpointStats>(),
                LiveTimeBuckets: LiveTimeBucketsOverride,
                LiveEndpointStats: LiveEndpointStatsOverride);
            return Task.FromResult(bundle);
        }

        // Prior-window helpers — allowed to be called by the controller directly.
        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            GetSummaryCallCount++;
            return Task.FromResult(EmptySummary());
        }

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            GetTopBotsCallCount++;
            return Task.FromResult(new List<DashboardTopBotEntry>());
        }

        // All other per-widget methods throw — if the controller calls them
        // for the current window it is a bug.
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        // The two live-tier reads must NEVER be called directly now that the overlay rides
        // in ComposeBatchAsync (DatasetKind.LiveTimeBuckets / LiveEndpointStats) -- if the
        // controller regresses to a direct call this throws instead of silently passing via
        // the interface DIM default (the exact silent-empty class the fold fixed).
        public Task<IReadOnlyList<DashboardTimeSeriesPoint>> GetLiveTimeSeriesAsync(
            DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null)
            => throw new InvalidOperationException("Live time-series overlay must ride in ComposeBatchAsync (DatasetKind.LiveTimeBuckets), not a direct read.");
        public Task<IReadOnlyList<DashboardEndpointStats>> GetLiveEndpointStatsAsync(
            DateTime windowStart, DateTime windowEnd, string? audienceFilter = null)
            => throw new InvalidOperationException("Live endpoint overlay must ride in ComposeBatchAsync (DatasetKind.LiveEndpointStats), not a direct read.");
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

    // ---------- tests ----------

    [Fact]
    public async Task TrafficController_issues_one_compose_for_current_window()
    {
        // Arrange — wire up the real composer using the recording store
        var store = new RecordingEventStore();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifests = new DefaultDashboardPageManifestSource();

        var controller = new Mostlylucid.BotDetection.UI.Controllers.TrafficController(
            store, ContentCache(composer), manifests, DefaultLayoutOptions(), DefaultThreatsOptions());
        controller.ControllerContext = new ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };

        // Act
        _ = await controller.Index(country: null, botType: null, window: "6h", from: null, to: null, threat: null, partial: null, null, default);

        // Assert — the current window is composed once with its eight datasets (five
        // detection aggregates + site-health + the two live-tier overlays, folded in
        // 2026-08-22 per the render-once ruling so Traffic's live overlay no longer costs
        // two extra round trips), and the prior window is a SECOND batched compose
        // (top-bots + summary) — so both are batched, neither fans out.
        Assert.Equal(2, store.ComposeBatchCallCount);

        var currentKinds = new[]
        {
            DatasetKind.SummaryStats, DatasetKind.TimeBuckets, DatasetKind.BotAggregate,
            DatasetKind.GeoBreakdown, DatasetKind.EndpointStats, DatasetKind.DegradationHistory,
            DatasetKind.LiveTimeBuckets, DatasetKind.LiveEndpointStats,
        }.OrderBy(k => k).ToArray();
        Assert.Contains(store.BatchRequests,
            r => r.Datasets.Select(d => d.Kind).OrderBy(k => k).SequenceEqual(currentKinds));
    }

    [Fact]
    public async Task TrafficController_stashes_page_result_in_HttpContext_Items()
    {
        var store = new RecordingEventStore();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifests = new DefaultDashboardPageManifestSource();

        var httpContext = new DefaultHttpContext();
        var controller = new Mostlylucid.BotDetection.UI.Controllers.TrafficController(
            store, ContentCache(composer), manifests, DefaultLayoutOptions(), DefaultThreatsOptions());
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };

        _ = await controller.Index(country: null, botType: null, window: "6h", from: null, to: null, threat: null, partial: null, null, default);

        Assert.True(httpContext.Items.ContainsKey("sb.dashboard.pageresult"),
            "Expected 'sb.dashboard.pageresult' in HttpContext.Items after Index()");
        Assert.IsType<DashboardPageResult>(httpContext.Items["sb.dashboard.pageresult"]);
    }

    /// <summary>
    ///     Regression test for a real bug: the TimeBuckets branch (IncrementalTimeBucketStore)
    ///     has its own, heavier cold path than the other four compose-batch branches and can
    ///     independently return null even when the overall envelope is otherwise warm
    ///     (page.IsWarming == false). HitsPerPeriodChartletBuilder.BuildSeries always
    ///     gap-fills a full zero-value bucket axis regardless of whether the input was null
    ///     or genuinely empty, so Model.Timeseries.Buckets.Count is NEVER 0 -- the chart's
    ///     own empty-vs-warming distinction can only come from Model.IsWarming correctly
    ///     reflecting a null TimeBuckets, not from bucket count. Before the fix,
    ///     Model.IsWarming was wired to page.IsWarming alone, so this exact scenario
    ///     (envelope warm, TimeBuckets branch specifically cold) rendered a chart with a
    ///     full zero-bar axis and no warming spinner -- visually indistinguishable from a
    ///     genuinely quiet window.
    /// </summary>
    [Fact]
    public async Task TrafficController_flags_IsWarming_when_TimeBuckets_branch_is_null_even_if_envelope_is_warm()
    {
        var store = new RecordingEventStore { TimeBucketsOverride = null };
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifests = new DefaultDashboardPageManifestSource();

        // AutoWarmingContentCache simulates "the materializer already warmed this envelope"
        // -- page.IsWarming will be false here even though TimeBuckets is null, isolating
        // the fix's OR condition from the coarser envelope-level flag.
        var controller = new Mostlylucid.BotDetection.UI.Controllers.TrafficController(
            store, ContentCache(composer), manifests, DefaultLayoutOptions(), DefaultThreatsOptions());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Index(country: null, botType: null, window: "6h", from: null, to: null, threat: null, partial: null, null, default);

        var model = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result).Model as TrafficPageModel;
        Assert.NotNull(model);
        Assert.True(model!.IsWarming,
            "Expected Model.IsWarming to be true when TimeBuckets is null, even though the overall envelope reports warm.");
    }

    [Fact]
    public async Task TrafficController_prior_window_is_composed_via_the_batch_not_direct_calls()
    {
        // The prior-window comparison now goes through the content cache / composer (a
        // second batched compose of summary + top-bots), so it NO LONGER fans out via
        // direct GetTopBotsAsync / GetSummaryAsync — the whole page reads out-of-request.
        var store = new RecordingEventStore();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifests = new DefaultDashboardPageManifestSource();

        var controller = new Mostlylucid.BotDetection.UI.Controllers.TrafficController(
            store, ContentCache(composer), manifests, DefaultLayoutOptions(), DefaultThreatsOptions());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        _ = await controller.Index(country: null, botType: null, window: "6h", from: null, to: null, threat: null, partial: null, null, default);

        // No direct prior-window fetches — it's batched instead.
        Assert.Equal(0, store.GetTopBotsCallCount + store.GetSummaryCallCount);

        // A second compose exists for the prior window: exactly summary + top-bots.
        var priorKinds = new[] { DatasetKind.SummaryStats, DatasetKind.BotAggregate }.OrderBy(k => k).ToArray();
        Assert.Contains(store.BatchRequests,
            r => r.Datasets.Select(d => d.Kind).OrderBy(k => k).SequenceEqual(priorKinds));
    }

    [Fact]
    public async Task Pinned_window_without_the_boot_coordinator_serves_warming_once_no_poll()
    {
        // The complete-cache serve (operator 2026-08-15 — "the JSON loads WITH the page,
        // NOT afterwards. A spinner should be IMPOSSIBLE"): a pinned window's serve AWAITS
        // the boot pass's completion (the coordinator's BootWarmCompletion) and then
        // serves the complete cached page — never a poll loop, never a rescue fill. A
        // host without the coordinator is misconfigured: the Warming serves as-is (the
        // page either contains the data or the host is broken — no self-heal window).
        var cache = new Mock<IDashboardContentCache>();
        cache.Setup(c => c.GetCurrentAsync(
                It.IsAny<DashboardPageManifest>(), It.IsAny<DashboardPageWindow>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(DashboardPageResult.Warming);

        var controller = new Mostlylucid.BotDetection.UI.Controllers.TrafficController(
            new RecordingEventStore(),
            cache.Object,
            new DefaultDashboardPageManifestSource(),
            DefaultLayoutOptions(),
            DefaultThreatsOptions())
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Index(null, null, null, null, null, null, null, null, CancellationToken.None);

        Assert.IsType<ViewResult>(result);
        // The page result is the Warming as-served — the old first-paint poll loop is
        // gone by design (a host without the boot coordinator is misconfigured; the
        // page either contains the data or the host is broken — no self-heal window).
        Assert.Same(DashboardPageResult.Warming, controller.HttpContext.Items["sb.dashboard.pageresult"]);
    }

    [Fact]
    public async Task TrafficController_live_overlay_merges_over_base_timeseries_from_compose()
    {
        // Render-once ruling (2026-08-22): Traffic's live time-series overlay rides in the
        // SAME ComposeBatchAsync call (DatasetKind.LiveTimeBuckets) as the rest of the page.
        // The merge must make the LIVE value win at a timestamp where both base and live
        // have a bucket, and preserve the base value where only base has one. With the
        // live reads on the store throwing, any regression to a direct per-widget read
        // fails this test loudly instead of silently passing via the DIM default.
        var now = DateTime.UtcNow;
        var tLive = now.AddMinutes(-3);   // in the last 5-min bucket of the 6h window
        var tBase = now.AddMinutes(-10);  // second-to-last bucket, base-only
        var store = new RecordingEventStore
        {
            TimeBucketsOverride = new List<DashboardTimeSeriesPoint>
            {
                new() { Timestamp = tLive, BotCount = 5, HumanCount = 1, TotalCount = 6 },
                new() { Timestamp = tBase, BotCount = 7, HumanCount = 1, TotalCount = 8 },
            },
            LiveTimeBucketsOverride = new List<DashboardTimeSeriesPoint>
            {
                // The EXACT same timestamp as the base point -- live must REPLACE it.
                new() { Timestamp = tLive, BotCount = 42, HumanCount = 2, TotalCount = 44 },
            },
        };
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifests = new DefaultDashboardPageManifestSource();
        var controller = new Mostlylucid.BotDetection.UI.Controllers.TrafficController(
            store, ContentCache(composer), manifests, DefaultLayoutOptions(), DefaultThreatsOptions());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Index(country: null, botType: null, window: "6h", from: null, to: null, threat: null, partial: null, null, default);
        var model = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result).Model as TrafficPageModel;
        Assert.NotNull(model);

        var ts = model!.Timeseries;
        Assert.NotEmpty(ts.Buckets);
        // BuildSeries aligns Buckets[0] DOWN to the 5-min boundary, so the point index is
        // exactly (t - Buckets[0]) / 5min -- same grid the builder uses.
        int BucketIndexOf(DateTime t)
        {
            var idx = (int)Math.Floor((t - ts.Buckets[0]).TotalMinutes / 5.0);
            return Math.Clamp(idx, 0, ts.Buckets.Count - 1);
        }

        var liveIdx = BucketIndexOf(tLive);
        var baseIdx = BucketIndexOf(tBase);
        Assert.Equal(42, ts.Bot[liveIdx]);  // LIVE value won the merge at the shared timestamp
        Assert.Equal(7, ts.Bot[baseIdx]);   // base-only bucket preserved (a merge, not live-only)
    }

    [Fact]
    public async Task TrafficController_live_endpoints_overlay_merges_from_compose()
    {
        // Same ruling for the endpoints overlay: the live endpoint rows ride in the compose
        // bundle (DatasetKind.LiveEndpointStats) and must surface in TopEndpoints.
        var store = new RecordingEventStore
        {
            LiveEndpointStatsOverride = new List<DashboardEndpointStats>
            {
                new() { Method = "GET", Path = "/live-only", TotalCount = 42, BotCount = 42, BotRate = 1.0 },
            },
        };
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifests = new DefaultDashboardPageManifestSource();
        var controller = new Mostlylucid.BotDetection.UI.Controllers.TrafficController(
            store, ContentCache(composer), manifests, DefaultLayoutOptions(), DefaultThreatsOptions());
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() };

        var result = await controller.Index(country: null, botType: null, window: "6h", from: null, to: null, threat: null, partial: null, null, default);
        var model = Assert.IsType<Microsoft.AspNetCore.Mvc.ViewResult>(result).Model as TrafficPageModel;
        Assert.NotNull(model);

        var row = Assert.Single(model!.TopEndpoints, r => r.Path == "/live-only");
        Assert.Equal("GET", row.Method);
        Assert.Equal(42, row.Hits);
    }
}
