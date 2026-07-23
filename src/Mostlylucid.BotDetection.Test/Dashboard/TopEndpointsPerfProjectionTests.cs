using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.UI.Controllers;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     U4: Top Endpoints widget surfaces per-endpoint performance summary.
///     Each <see cref="EndpointRow"/> carries p95 latency (ms), error %, and
///     a sample count drawn straight from the existing
///     <see cref="DashboardEndpointStats"/> projection -- no parallel atom,
///     no parallel REST endpoint, no parallel sampler.
///     <para>
///         Endpoints with fewer than
///         <see cref="DashboardLayoutOptions.TopEndpointsMinSamplesForPerf"/>
///         observations contribute <c>HasPerf=false</c> so the view renders a
///         dash placeholder instead of a misleading number on cold-start /
///         brand-new paths.
///     </para>
/// </summary>
public sealed class TopEndpointsPerfProjectionTests
{
    [Fact]
    public async Task TopByEndpoint_carries_p95_and_err_pct_from_store_stats()
    {
        var store = new PerfTestEventStore
        {
            Endpoints = new List<DashboardEndpointStats>
            {
                new()
                {
                    Method = "GET",
                    Path = "/api/v1/things",
                    TotalCount = 120,
                    BotCount = 12,
                    BotRate = 0.1,
                    P95ProcessingTimeMs = 42.5,
                    Status4xx = 6,   // 5% error
                    Status5xx = 0,
                    LastSeen = DateTime.UtcNow,
                },
            },
        };
        var ctrl = NewController(store);

        var result = await ctrl.Index(
            country: null, botType: null, window: "60m", threat: null, partial: null, ct: default);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TrafficPageModel>(view.Model);
        var row = Assert.Single(model.TopEndpoints);
        Assert.Equal("GET", row.Method);
        Assert.Equal("/api/v1/things", row.Path);
        Assert.Equal(120, row.Hits);
        Assert.Equal(42.5, row.P95Ms);
        // 6 / 120 = 0.05 = 5%
        Assert.Equal(0.05, row.ErrorRate, 3);
        Assert.True(row.HasPerf);
    }

    [Fact]
    public async Task TopByEndpoint_marks_low_sample_count_as_no_perf()
    {
        // Default MinSamplesForPerf is 3; two samples should NOT show perf.
        var store = new PerfTestEventStore
        {
            Endpoints = new List<DashboardEndpointStats>
            {
                new()
                {
                    Method = "GET",
                    Path = "/new-page",
                    TotalCount = 2,
                    BotCount = 0,
                    BotRate = 0.0,
                    P95ProcessingTimeMs = 99.0,
                    Status4xx = 0,
                    Status5xx = 0,
                    LastSeen = DateTime.UtcNow,
                },
            },
        };
        var ctrl = NewController(store);

        var result = await ctrl.Index(
            country: null, botType: null, window: "60m", threat: null, partial: null, ct: default);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TrafficPageModel>(view.Model);
        var row = Assert.Single(model.TopEndpoints);
        Assert.False(row.HasPerf);
    }

    [Fact]
    public async Task TopByEndpoint_combines_status_4xx_and_5xx_into_error_rate()
    {
        var store = new PerfTestEventStore
        {
            Endpoints = new List<DashboardEndpointStats>
            {
                new()
                {
                    Method = "POST",
                    Path = "/checkout",
                    TotalCount = 100,
                    BotCount = 0,
                    BotRate = 0.0,
                    P95ProcessingTimeMs = 250.0,
                    Status4xx = 4,
                    Status5xx = 6,
                    LastSeen = DateTime.UtcNow,
                },
            },
        };
        var ctrl = NewController(store);

        var result = await ctrl.Index(
            country: null, botType: null, window: "60m", threat: null, partial: null, ct: default);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TrafficPageModel>(view.Model);
        var row = Assert.Single(model.TopEndpoints);
        // (4 + 6) / 100 = 0.1
        Assert.Equal(0.10, row.ErrorRate, 3);
    }

    private static TrafficController NewController(PerfTestEventStore store)
    {
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var contentCache = new Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardContentCache(
            (m, w, ct) => composer.ComposeAsync(m, w, ct), () => 1L,
            new Mostlylucid.BotDetection.Test.Helpers.MutableOptionsMonitor<Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardMaterializerOptions>(new Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardMaterializerOptions()));
        var manifests = new DefaultDashboardPageManifestSource();
        var controller = new TrafficController(
            store,
            contentCache,
            manifests,
            Options.Create(new DashboardLayoutOptions()),
            Options.Create(new ThreatsOptions()));
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new DefaultHttpContext(),
        };
        return controller;
    }

    /// <summary>
    ///     Stub <see cref="IDashboardEventStore"/> that only fills the endpoint
    ///     projection -- every other method returns empty so the controller's
    ///     SafeGet* guards don't blow up.
    /// </summary>
    private sealed class PerfTestEventStore : IDashboardEventStore
    {
        public List<DashboardEndpointStats> Endpoints { get; set; } = new();

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
        {
            var summary = new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0,
            };
            return Task.FromResult(new DashboardDatasetBundle(
                Summary: summary,
                TimeBuckets: new List<DashboardTimeSeriesPoint>(),
                BotAggregate: new List<DashboardTopBotEntry>(),
                Geo: new List<DashboardCountryStats>(),
                Endpoints: Endpoints));
        }

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(Endpoints);

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardTopBotEntry>());

        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0,
            });

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardCountryStats>());

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => Task.CompletedTask;
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => Task.FromResult(signature);
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => Task.CompletedTask;
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => Task.FromResult(new List<DashboardDetectionEvent>());
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => Task.FromResult(new List<DashboardSignatureEvent>());
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => Task.FromResult(new List<DashboardTimeSeriesPoint>());
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult<DashboardCountryDetail?>(null);
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => Task.FromResult(new List<SignatureEndpointStats>());
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult<DashboardEndpointDetail?>(null);
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null) => Task.FromResult(new List<ThreatEntry>());
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => Task.FromResult(new List<UserAgentSearchResult>());
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => Task.FromResult(new List<UserAgentVersionBucket>());
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => Task.FromResult(new List<HoneypotHitRow>());
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordDegradationSnapshotAsync(
            Mostlylucid.BotDetection.RateLimit.DegradationSnapshot snapshot,
            CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>>
            GetDegradationHistoryAsync(DateTime startTime, DateTime endTime,
                CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>>(
                Array.Empty<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>());
    }
}
