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
                TimeBuckets: new List<DashboardTimeSeriesPoint>(),
                BotAggregate: new List<DashboardTopBotEntry>(),
                Geo: new List<DashboardCountryStats>(),
                Endpoints: new List<DashboardEndpointStats>());
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
        _ = await controller.Index(country: null, botType: null, window: "6h", threat: null, partial: null, default);

        // Assert — the current window is composed once with its six datasets (five
        // detection aggregates + site-health), and the prior window is a SECOND batched
        // compose (top-bots + summary) — so both are batched, neither fans out.
        Assert.Equal(2, store.ComposeBatchCallCount);

        var currentKinds = new[]
        {
            DatasetKind.SummaryStats, DatasetKind.TimeBuckets, DatasetKind.BotAggregate,
            DatasetKind.GeoBreakdown, DatasetKind.EndpointStats, DatasetKind.DegradationHistory,
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

        _ = await controller.Index(country: null, botType: null, window: "6h", threat: null, partial: null, default);

        Assert.True(httpContext.Items.ContainsKey("sb.dashboard.pageresult"),
            "Expected 'sb.dashboard.pageresult' in HttpContext.Items after Index()");
        Assert.IsType<DashboardPageResult>(httpContext.Items["sb.dashboard.pageresult"]);
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

        _ = await controller.Index(country: null, botType: null, window: "6h", threat: null, partial: null, default);

        // No direct prior-window fetches — it's batched instead.
        Assert.Equal(0, store.GetTopBotsCallCount + store.GetSummaryCallCount);

        // A second compose exists for the prior window: exactly summary + top-bots.
        var priorKinds = new[] { DatasetKind.SummaryStats, DatasetKind.BotAggregate }.OrderBy(k => k).ToArray();
        Assert.Contains(store.BatchRequests,
            r => r.Datasets.Select(d => d.Kind).OrderBy(k => k).SequenceEqual(priorKinds));
    }
}