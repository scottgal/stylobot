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
///     Si2 (endpoint-IA unification), task 1: proves the URL-bound filters actually
///     bind on the FIRST page load of the canonical control's landing page
///     (<c>{basePath}/site</c>), not just on a follow-up hx-get triggered by clicking
///     a chip inside an already-rendered (unfiltered) list. Before wiring
///     _Endpoints.cshtml to read <c>Context.Request.Query</c> and forward it through
///     &lt;sb-endpoints-list&gt;'s new path/method/mode/status/threat/bot-pressure
///     attributes (SbEndpointsListTagHelperForwardingTests covers the forwarding
///     itself in isolation), the bare <c>&lt;sb-endpoints-list /&gt;</c> call ignored
///     the query string entirely on first paint.
/// </summary>
public sealed class SitePageUrlBoundFilterIntegrationTests : IAsyncDisposable
{
    private WebApplication? _app;

    private async Task<WebApplication> BuildAppAsync()
    {
        var store = new FakeStore();
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
    public async Task Site_page_first_paint_is_already_filtered_by_the_query_string()
    {
        var app = await BuildAppAsync();

        var response = await app.GetTestClient().GetAsync("/dashboard/site?method=POST");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("/api/orders", html);   // POST row: kept
        Assert.DoesNotContain("/pricing", html); // GET row: filtered out on FIRST paint
    }

    [Fact]
    public async Task Site_page_with_no_query_string_renders_every_row()
    {
        var app = await BuildAppAsync();

        var html = await app.GetTestClient().GetStringAsync("/dashboard/site");

        Assert.Contains("/api/orders", html);
        Assert.Contains("/pricing", html);
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }

    private sealed class FakeStore : IDashboardEventStore
    {
        public Task<DashboardDatasetBundle> ComposeBatchAsync(DashboardBatchRequest request, CancellationToken ct = default) =>
            Task.FromResult(new DashboardDatasetBundle(
                Summary: new DashboardSummary { Timestamp = DateTime.UtcNow, TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0, UniqueSignatures = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new() },
                TimeBuckets: new List<DashboardTimeSeriesPoint>(),
                BotAggregate: new List<DashboardTopBotEntry>(),
                Geo: new List<DashboardCountryStats>(),
                Endpoints: new List<DashboardEndpointStats>
                {
                    new() { Method = "GET", Path = "/pricing", TotalCount = 10 },
                    new() { Method = "POST", Path = "/api/orders", TotalCount = 5 },
                }));

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
        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) =>
            Task.FromResult(new List<DashboardEndpointStats>
            {
                new() { Method = "GET", Path = "/pricing", TotalCount = 10 },
                new() { Method = "POST", Path = "/api/orders", TotalCount = 5 },
            });
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
