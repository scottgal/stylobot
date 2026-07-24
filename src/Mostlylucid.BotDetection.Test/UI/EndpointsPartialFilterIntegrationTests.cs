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
///     Si2 (endpoint-IA unification): the interactive sort/filter chips inside
///     SbEndpointsList/Default.cshtml hx-get <c>{basePath}/partials/endpoints</c>
///     (<see cref="StyloBotDashboardMiddleware"/>'s ServeEndpointsPartialAsync), a
///     SEPARATE code path from the view component's own InvokeAsync filtering
///     (covered by SbEndpointsListModeMethodFilterTests). This proves the new
///     method/mode query params filter correctly end-to-end through a real
///     TestServer round trip, and that the active filter round-trips onto the
///     rendered chip markup so a follow-up sort/page link doesn't silently drop it.
/// </summary>
public sealed class EndpointsPartialFilterIntegrationTests : IAsyncDisposable
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
    public async Task Method_query_param_filters_the_rendered_rows()
    {
        var app = await BuildAppAsync();

        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints?method=POST");

        Assert.Contains("/api/orders", html);
        Assert.DoesNotContain("/pricing", html);
        Assert.DoesNotContain("/app.js", html);
    }

    [Fact]
    public async Task Mode_query_param_filters_to_the_api_bucket_only()
    {
        var app = await BuildAppAsync();

        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints?mode=api");

        Assert.Contains("/api/orders", html);
        Assert.DoesNotContain("/pricing", html);   // content
        Assert.DoesNotContain("/app.js", html);    // static
    }

    [Fact]
    public async Task Combined_method_and_mode_filters_compose()
    {
        var app = await BuildAppAsync();

        var response = await app.GetTestClient().GetAsync("/dashboard/partials/endpoints?method=GET&mode=api");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        // GET /api/orders doesn't exist in the fixture (only POST does) -- GET+api
        // should therefore render the "no endpoint analytics" empty state, proving
        // both predicates are applied (AND, not OR).
        Assert.DoesNotContain("/api/orders", html);
    }

    /// <summary>
    ///     Internal-filter posture: the "Show self-probe" toggle -> <c>audience=
    ///     all_incl_internal</c> -- must route <c>ServeEndpointsPartialAsync</c> through
    ///     the store (the audience-agnostic cache/GetEndpointsDataAsync path can never
    ///     contain Internal rows, since the store's own default now excludes them), and
    ///     the /metrics row (visible only under this audience per the fixture store) must
    ///     render. Proves the wiring added to ServeEndpointsPartialAsync's storeFilters
    ///     check, not just the AudiencePredicate switch it delegates to.
    /// </summary>
    [Fact]
    public async Task ShowSelfProbe_audience_query_param_routes_through_the_store_and_reveals_internal_rows()
    {
        var app = await BuildAppAsync();

        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints?audience=all_incl_internal");

        Assert.Contains("/metrics", html);
        // Chip markup itself must render in its "active" (highlighted) state -- the same
        // warning-colored class AudienceChipClass gives every other active audience chip
        // (mirrors the Honeypot chip's own highlight convention).
        Assert.Contains("Show self-probe", html);
        Assert.Contains("bg-warning/20 text-warning border border-warning/30", html);
    }

    /// <summary>Default (no audience param) must NOT reveal the Internal-only endpoint --
    ///     pins the new default posture on the same integration path -- and the chip must
    ///     render in its inactive state.</summary>
    [Fact]
    public async Task Default_audience_hides_the_internal_only_endpoint()
    {
        var app = await BuildAppAsync();

        var html = await app.GetTestClient().GetStringAsync("/dashboard/partials/endpoints");

        Assert.DoesNotContain("/metrics", html);
        Assert.Contains("Show self-probe", html);
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

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            var rows = new List<DashboardEndpointStats>
            {
                new() { Method = "GET", Path = "/pricing", TotalCount = 10 },
                new() { Method = "POST", Path = "/api/orders", TotalCount = 5 },
                new() { Method = "GET", Path = "/app.js", TotalCount = 30 },
            };
            // /metrics is pure self-probe (Internal) traffic: mirrors the real
            // AudiencePredicate/ComposeAudiencePredicate contract (excluded by default,
            // visible only under "internal" / "all" / "all_incl_internal").
            if (audienceFilter is "internal" or "all" or "all_incl_internal")
                rows.Add(new() { Method = "GET", Path = "/metrics", TotalCount = 4 });
            return Task.FromResult(rows);
        }

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
