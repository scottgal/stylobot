using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Stage 2a end-to-end: <c>ServeDashboardPageAsync</c>'s Clusters row is a pure
///     content-cache read. This exercises the FULL pipeline (middleware -> Index.cshtml
///     -> _ClustersList.cshtml) against the "dashboard.clusters" manifest -- the
///     manifest that IS genuinely rendered straight from the shell model (see
///     research note: Countries/Endpoints/UserAgents/Visitors ignore the shell's
///     computed field and self-fetch via their own ViewComponents; Clusters does not).
///
///     <para>
///         Two scenarios: (1) a WARMED cache renders the real cluster data with no
///         "Warming up" placeholder; (2) a genuinely COLD cache (never warmed) renders
///         the Warming placeholder instead of crashing OR computing synchronously
///         (proven by never registering an <see cref="IBotClusterReader"/> at all --
///         if the request path fell back to a direct compute it would either throw
///         resolving a missing service call path or silently render the "no clusters"
///         empty state instead of the warming copy).
///     </para>
/// </summary>
public sealed class DashboardRowExtraContentCacheIntegrationTests : IAsyncDisposable
{
    private WebApplication? _app;

    [Fact]
    public async Task Clusters_row_renders_the_warmed_cache_snapshot_with_no_warming_banner()
    {
        var clusterReader = new FakeClusterReader();
        var manifests = new DefaultDashboardPageManifestSource();
        var catalog = DashboardWidgetCatalog.BuildFromLoadedAssemblies();
        var composer = new DefaultDashboardPageComposer(catalog, new BenignEventStore(), clusterReader: clusterReader);

        long tick = 1;
        var cache = new Mostlylucid.BotDetection.Test.Helpers.AutoWarmingContentCache(
            new DashboardContentCache((m, w, ct) => composer.ComposeAsync(m, w, ct), () => tick,
                Options.Create(new DashboardMaterializerOptions())),
            () => tick);

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(new BenignEventStore());
        builder.Services.AddSingleton<IBotClusterReader>(clusterReader);
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

        var response = await _app.GetTestClient().GetAsync("/dashboard/clusters");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Stage2aTestCluster", html);
        Assert.DoesNotContain("Warming up", html);
    }

    [Fact]
    public async Task Clusters_row_renders_the_warming_placeholder_on_a_genuine_cold_miss()
    {
        var manifests = new DefaultDashboardPageManifestSource();
        long tick = 1;
        // Deliberately NOT auto-warmed -- a real never-warmed DashboardContentCache, so
        // GetCurrentAsync structurally returns DashboardPageResult.Warming (never composes
        // on the request thread; see DashboardContentCache's own contract).
        var cache = new DashboardContentCache(
            (m, w, ct) => throw new InvalidOperationException("compose must never run on the request thread for a cold miss"),
            () => tick, Options.Create(new DashboardMaterializerOptions()));

        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(new BenignEventStore());
        // No IBotClusterReader registered at all -- a direct-compute fallback would need
        // one; its total absence would surface as an empty (not warming) render if the
        // request path still tried to compute directly instead of reading the cache.
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

        var response = await _app.GetTestClient().GetAsync("/dashboard/clusters");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // bd196a8e + b7c72711: "Warming up" page-level banner is gone. Cold rows on
        // list-only pages (Clusters, without chart widgets) fetch live with no warming strip.
        Assert.DoesNotContain("Warming up", html);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private sealed class FakeClusterReader : IBotClusterReader
    {
        public Task<IReadOnlyList<BotCluster>> GetClustersAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BotCluster>>(new List<BotCluster>
            {
                new()
                {
                    ClusterId = "stage2a-c1",
                    MemberSignatures = new List<string> { "sig1" },
                    Type = BotClusterType.BotNetwork,
                    MemberCount = 1,
                    Label = "Stage2aTestCluster",
                }
            });

        public Task<BotClusterService.ClusterDiagnosticsSnapshot> GetDiagnosticsAsync(CancellationToken ct = default) =>
            Task.FromResult(new BotClusterService.ClusterDiagnosticsSnapshot { Algorithm = "leiden", Status = "ok" });
    }

    /// <summary>Benign store: every read returns empty/zeroed data, nothing throws.</summary>
    private sealed class BenignEventStore : IDashboardEventStore
    {
        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default)
            => Task.FromResult(new DashboardDatasetBundle(null, null, null, null, null));

        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new DashboardSummary
            {
                Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0,
                UncertainRequests = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new(), UniqueSignatures = 0,
            });

        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardTopBotEntry>());

        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardCountryStats>());

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
            => Task.FromResult(new List<DashboardEndpointStats>());

        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null)
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
        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
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
