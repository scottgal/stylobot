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
///     Pins the two controller behaviours C1 added: the <c>?bot_type=</c> query
///     binding actually narrows the projection so a Scraper drill returns a
///     model whose top-visitors are all Scrapers, and an <c>HX-Request</c>
///     header short-circuits the response to just the <c>_TrafficPanels</c>
///     partial (chart + counters + filter chrome stay put on the live page).
/// </summary>
public sealed class TrafficControllerFilterTests
{
    [Fact]
    public async Task Index_with_hx_request_returns_traffic_panels_partial()
    {
        var ctrl = NewController(out var http);
        http.Request.Headers["HX-Request"] = "true";

        var result = await ctrl.Index(
            country: null, botType: "Scraper", window: "60m", threat: null, partial: null, ct: default);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.EndsWith("_TrafficPanels.cshtml", partial.ViewName);
        var model = Assert.IsType<TrafficPageModel>(partial.Model);
        Assert.Equal("Scraper", model.Filters.BotType);
    }

    [Fact]
    public async Task Index_without_hx_request_returns_full_view()
    {
        var ctrl = NewController(out _);

        var result = await ctrl.Index(
            country: null, botType: null, window: "60m", threat: null, partial: null, ct: default);

        var view = Assert.IsType<ViewResult>(result);
        Assert.EndsWith("Index.cshtml", view.ViewName);
    }

    [Fact]
    public async Task Index_with_partial_one_returns_body_partial_even_when_hx_request_present()
    {
        var ctrl = NewController(out var http);
        http.Request.Headers["HX-Request"] = "true";

        var result = await ctrl.Index(
            country: null, botType: null, window: "60m", threat: null, partial: 1, ct: default);

        var partial = Assert.IsType<PartialViewResult>(result);
        Assert.EndsWith("_Body.cshtml", partial.ViewName);
    }

    [Fact]
    public async Task Index_with_bot_type_filter_narrows_top_visitors_to_that_family()
    {
        var store = new TestEventStore
        {
            TopBots = new List<DashboardTopBotEntry>
            {
                new() { PrimarySignature = "a", HitCount = 5, BotType = "Scraper", BotProbability = 0.95, FirstSeen = DateTime.UtcNow.AddMinutes(-10), LastSeen = DateTime.UtcNow.AddMinutes(-1) },
                new() { PrimarySignature = "b", HitCount = 3, BotType = "SearchEngine", BotProbability = 0.9,  FirstSeen = DateTime.UtcNow.AddMinutes(-10), LastSeen = DateTime.UtcNow.AddMinutes(-1) },
                new() { PrimarySignature = "c", HitCount = 7, BotType = "Scraper", BotProbability = 0.97, FirstSeen = DateTime.UtcNow.AddMinutes(-10), LastSeen = DateTime.UtcNow.AddMinutes(-2) },
            }
        };
        var ctrl = NewController(out _, store);

        var result = await ctrl.Index(
            country: null, botType: "Scraper", window: "60m", threat: null, partial: null, ct: default);

        var view = Assert.IsType<ViewResult>(result);
        var model = Assert.IsType<TrafficPageModel>(view.Model);
        Assert.NotEmpty(model.TopVisitors);
        Assert.All(model.TopVisitors, v =>
            Assert.Equal("Scraper", v.BotType, ignoreCase: true));
    }

    private static TrafficController NewController(out DefaultHttpContext httpContext, TestEventStore? store = null)
    {
        store ??= new TestEventStore();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var contentCache = new Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardContentCache(
            (m, w, ct) => composer.ComposeAsync(m, w, ct), () => 1L,
            Options.Create(new Mostlylucid.BotDetection.UI.Dashboard.Materialization.DashboardMaterializerOptions()));
        var manifests = new DefaultDashboardPageManifestSource();
        var controller = new TrafficController(
            store,
            contentCache,
            manifests,
            Options.Create(new DashboardLayoutOptions()),
            Options.Create(new ThreatsOptions()));
        httpContext = new DefaultHttpContext();
        controller.ControllerContext = new ControllerContext { HttpContext = httpContext };
        return controller;
    }

    /// <summary>
    ///     Minimal in-memory <see cref="IDashboardEventStore"/> for the
    ///     controller tests. Only the methods the controller actually calls
    ///     need real behaviour -- the rest return empty / null so the
    ///     <c>SafeGet*Async</c> guards in the controller don't blow up.
    /// </summary>
    private sealed class TestEventStore : IDashboardEventStore
    {
        public List<DashboardTopBotEntry> TopBots { get; set; } = new();

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
                BotAggregate: TopBots,
                Geo: new List<DashboardCountryStats>(),
                Endpoints: new List<DashboardEndpointStats>()));
        }

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(TopBots);

        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0,
                BotRequests = 0,
                HumanRequests = 0,
                UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 0
            });

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardCountryStats>());

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardEndpointStats>());

        // Stubs for methods the controller doesn't touch on this path. They
        // exist only to satisfy the interface; the tests never invoke them.
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
