using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     TDD for the p99=10s bimodal-tail fix: <c>_Traffic.cshtml</c> (the "traffic" row
///     partial rendered by <see cref="StyloBotDashboardMiddleware.ServeDashboardPageAsync"/>
///     for the default "/{BasePath}/traffic" page) used to @inject
///     <see cref="IDashboardEventStore"/> directly and call GetSummaryAsync / GetTopBotsAsync
///     (current + prior) / GetCountryStatsAsync / GetEndpointStatsAsync / GetTimeSeriesAsync
///     IN THE VIEW, on every render -- a live cross-service round-trip on the request
///     thread on the website deployment topology (IDashboardEventStore = RemoteDashboardEventStore).
///
///     <para>
///         This exercises the FULL pipeline (middleware -&gt; Index.cshtml -&gt; _Traffic.cshtml)
///         the same way <see cref="DashboardRowExtraContentCacheIntegrationTests"/> does for the
///         Clusters row: (1) a WARMED cache renders real data with no "Warming up" placeholder and
///         issues zero <see cref="IDashboardEventStore.GetTimeSeriesAsync"/> calls -- a clean,
///         unambiguous discriminator because nothing else in this request path (only the old,
///         buggy _Traffic.cshtml and the separate, unreached MVC TrafficController) ever calls
///         it; (2) a genuinely COLD cache (a composer that throws if ever invoked) renders the
///         Warming placeholder instead of crashing or falling back to a direct store call.
///     </para>
/// </summary>
public sealed class TrafficRowContentCacheIntegrationTests : IAsyncDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Traffic_row_renders_the_warmed_cache_snapshot_with_no_warming_banner_and_no_direct_timeseries_call()
    {
        var store = new RecordingEventStore();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, store);
        var manifests = new DefaultDashboardPageManifestSource();

        long tick = 1;
        var cache = new Mostlylucid.BotDetection.Test.Helpers.AutoWarmingContentCache(
            new DashboardContentCache((m, w, ct) => composer.ComposeAsync(m, w, ct), () => tick,
                Options.Create(new DashboardMaterializerOptions())),
            () => tick);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(store);
        builder.Services.AddSingleton<IDashboardContentCache>(cache);
        builder.Services.AddSingleton<IDashboardPageManifestSource>(manifests);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

        _app = builder.Build();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var response = await _app.GetTestClient().GetAsync("/dashboard/traffic");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Stage3aTestBot", html);
        Assert.DoesNotContain("Warming up", html);

        // The unambiguous proof: GetTimeSeriesAsync is the one IDashboardEventStore method
        // that ONLY the pre-fix _Traffic.cshtml (and the separate, unreached MVC
        // TrafficController) ever call in this request path -- ServeDashboardPageAsync's own
        // shell-model fallbacks never touch it. Zero calls proves the traffic row's hits-per-
        // period chart came from the composed content-cache bundle, not a direct store hit.
        Assert.Equal(0, store.GetTimeSeriesCallCount);

        // Two composes: the current window's six-dataset batch (summary, time-chart,
        // top-bots, countries, endpoints, site-health) plus the disjoint prior-window
        // batch (summary + top-bots only) that the counter-strip delta needs.
        Assert.True(store.ComposeBatchCallCount >= 2,
            $"Expected at least 2 ComposeBatchAsync calls (current + prior window), got {store.ComposeBatchCallCount}");
    }

    [Fact]
    public async Task Traffic_row_renders_the_warming_placeholder_on_a_genuine_cold_miss()
    {
        var store = new RecordingEventStore();
        var manifests = new DefaultDashboardPageManifestSource();
        long tick = 1;
        // Deliberately NOT auto-warmed -- a real never-warmed DashboardContentCache, so
        // GetCurrentAsync structurally returns DashboardPageResult.Warming (never composes
        // on the request thread; see DashboardContentCache's own contract). The factory
        // throws if it is EVER invoked, proving a cold miss never falls back to a
        // synchronous compute -- and RecordingEventStore's per-widget methods aren't
        // hit directly either.
        var cache = new DashboardContentCache(
            (m, w, ct) => throw new InvalidOperationException("compose must never run on the request thread for a cold miss"),
            () => tick, Options.Create(new DashboardMaterializerOptions()));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(store);
        builder.Services.AddSingleton<IDashboardContentCache>(cache);
        builder.Services.AddSingleton<IDashboardPageManifestSource>(manifests);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

        _app = builder.Build();
        _app.UseMiddleware<StyloBotDashboardMiddleware>();
        await _app.StartAsync();

        var response = await _app.GetTestClient().GetAsync("/dashboard/traffic");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // dashboard-graph-quality PART 2: the standalone page-level "Warming up — traffic
        // data will appear shortly." banner (which rendered ABOVE a still-painting empty
        // chart canvas -- warming AND a bare chart at once) was replaced by the chart
        // widget's own three-state warming strip, so a cold miss shows warming-OR-chart,
        // never both. The warming signal now lives on the hits-per-period chart itself.
        // The em-dash in the copy is HTML-entity-encoded by the widget's HtmlEncoder, so
        // assert on the two ASCII halves rather than the literal "—".
        Assert.Contains("Warming up", html);
        Assert.Contains("chart data will appear shortly.", html);
        Assert.Equal(0, store.GetTimeSeriesCallCount);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    /// <summary>
    ///     Records every IDashboardEventStore call. ComposeBatchAsync succeeds with a
    ///     canned bundle (built directly, NOT delegated to DashboardEventStoreBatchDefaults,
    ///     so it never internally calls GetTimeSeriesAsync etc. itself -- keeping the
    ///     per-widget call counts an honest signal of direct callers). The per-widget
    ///     methods succeed (rather than throw) so any OTHER, unrelated legitimate caller in
    ///     ServeDashboardPageAsync (e.g. the shell model's own Countries/Visitors fallbacks,
    ///     which are separate from the traffic row and out of this test's scope) doesn't
    ///     blow up the render -- only GetTimeSeriesAsync's count is asserted on, since it is
    ///     the one method uniquely attributable to the traffic row's own fetch.
    /// </summary>
    private sealed class RecordingEventStore : IDashboardEventStore
    {
        public int ComposeBatchCallCount { get; private set; }
        public int GetTimeSeriesCallCount { get; private set; }

        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
        {
            ComposeBatchCallCount++;
            var bundle = new DashboardDatasetBundle(
                Summary: new DashboardSummary
                {
                    Timestamp = DateTime.UtcNow, TotalRequests = 100, BotRequests = 40, HumanRequests = 60,
                    UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(),
                    UniqueSignatures = 10, HumanFingerprints = 6, BotFingerprints = 4,
                },
                TimeBuckets: new List<DashboardTimeSeriesPoint>(),
                BotAggregate: new List<DashboardTopBotEntry>
                {
                    new()
                    {
                        PrimarySignature = "sig-stage3a",
                        BotName = "Stage3aTestBot",
                        HitCount = 40,
                        BotProbability = 0.95,
                        BotType = "Scraper",
                        LastSeen = DateTime.UtcNow,
                    },
                },
                Geo: new List<DashboardCountryStats> { new() { CountryCode = "GB", TotalCount = 100, BotCount = 40 } },
                Endpoints: new List<DashboardEndpointStats> { new() { Path = "/", Method = "GET", TotalCount = 100 } });
            return Task.FromResult(bundle);
        }

        public Task<DashboardSummary> GetSummaryAsync(
            DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0,
                UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 0,
            });

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(
            int count = 10, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardTopBotEntry>());

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(
            int count = 20, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardCountryStats>());

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardEndpointStats>());

        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
            DateTime startTime, DateTime endTime, TimeSpan bucketSize,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            GetTimeSeriesCallCount++;
            return Task.FromResult(new List<DashboardTimeSeriesPoint>());
        }

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
            => Task.FromResult(new List<ThreatEntry>());

        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
            => Task.FromResult(new List<DashboardDetectionEvent>());

        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => Task.FromResult(0);
        public Task RecordDegradationSnapshotAsync(DegradationSnapshot snapshot, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<DegradationSnapshot>>(Array.Empty<DegradationSnapshot>());

        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<FilterCounts> GetVisitorSegmentCountsAsync(DateTime startTime, DateTime endTime, string? filter = null, string? country = null, string? botType = null, string? threat = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
    }
}
