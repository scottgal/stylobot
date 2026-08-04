using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Extensions;
using Mostlylucid.BotDetection.UI.Middleware;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Si2 (endpoint-IA unification), task 2: the endpoint-detail page used to render
///     _EndpointDetail.cshtml directly with isMainPage: true, which falls back to
///     whatever bare _ViewStart/_Layout the HOST provides -- no dashboard sidebar/drawer
///     at all. This is the exact "old legacy shell" bug _SignatureDetail.cshtml's own
///     header comment documents having been fixed for the signature-detail page; this
///     test proves the same fix for endpoint-detail: both the new
///     {basePath}/endpoint/{method}/{path} segment route AND the legacy
///     {basePath}/endpoint?method=&amp;path= query route now render INSIDE the shared
///     dashboard shell (Index.cshtml -- proven via the #sb-nav-drawer chrome marker that
///     only the shell emits), not standalone.
/// </summary>
public sealed class EndpointDetailChromeIntegrationTests : IAsyncDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> BuildAppAsync()
    {
        var store = new FakeEventStore();
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<IDashboardEventStore>(store);
        builder.Services.AddControllersWithViews().AddApplicationPart(typeof(StyloBotDashboardMiddleware).Assembly);
        builder.Services.AddStyloBotDashboard(options =>
        {
            options.BasePath = "/dashboard";
            options.RequireAuthentication = false;
            options.AllowUnauthenticatedAccess = true;
        });

        var app = builder.Build();
        app.UseMiddleware<StyloBotDashboardMiddleware>();
        await app.StartAsync();
        _app = app;
        return app;
    }

    [Fact]
    public async Task Segment_route_renders_inside_the_shared_dashboard_shell()
    {
        var app = await BuildAppAsync();

        var response = await app.GetTestClient().GetAsync("/dashboard/endpoint/GET/api/orders");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sb-nav-drawer", html);       // only Index.cshtml's shell emits this
        Assert.Contains("/api/orders", html);         // the endpoint's own path rendered
        Assert.Contains("data-endpoint-detail", html); // _EndpointDetail.cshtml's own marker
    }

    [Fact]
    public async Task Legacy_query_string_route_also_renders_inside_the_shared_dashboard_shell()
    {
        var app = await BuildAppAsync();

        var response = await app.GetTestClient().GetAsync("/dashboard/endpoint?method=GET&path=/api/orders");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("sb-nav-drawer", html);
        Assert.Contains("/api/orders", html);
    }

    [Fact]
    public async Task Segment_route_missing_path_segment_is_a_400()
    {
        var app = await BuildAppAsync();

        var response = await app.GetTestClient().GetAsync("/dashboard/endpoint/GET");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Breadcrumb_links_to_the_real_site_route_not_the_dead_tab_query_param()
    {
        var app = await BuildAppAsync();

        var html = await app.GetTestClient().GetStringAsync("/dashboard/endpoint/GET/api/orders");

        Assert.DoesNotContain("?tab=endpoints", html);
        Assert.Contains("/dashboard/site", html);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private sealed class FakeEventStore : IDashboardEventStore
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

        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null) =>
            Task.FromResult<DashboardEndpointDetail?>(new DashboardEndpointDetail
            {
                Method = method,
                Path = path,
                TotalCount = 42,
                BotCount = 4,
                UniqueSignatures = 3,
                TopActions = new(),
                TopCountries = new(),
                RiskBands = new(),
                TopBots = [],
                RecentDetections = [],
            });

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
