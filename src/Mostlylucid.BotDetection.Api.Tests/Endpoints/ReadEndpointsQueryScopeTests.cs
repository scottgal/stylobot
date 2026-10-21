using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Api.Endpoints;
using Mostlylucid.BotDetection.Dashboard.Data.Queries;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Dashboard.Composition;
using Mostlylucid.BotDetection.UI.Dashboard.Materialization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Api.Tests.Endpoints;

/// <summary>
///     Real-route contract proof for the scoped dashboard reads. The remote website
///     used to append <c>domain</c>/<c>audience</c> to <c>/api/v1/endpoints</c> and
///     <c>/api/v1/timeseries</c> while the handlers accepted neither, so a domain-scoped
///     dashboard silently served unscoped rows. These tests boot the actual minimal-API
///     route table and assert what the store received, not just what URL the client built.
/// </summary>
public sealed class ReadEndpointsQueryScopeTests
{
    private const string ApiKeyId = "scope-test-key";
    private const string ApiKeyHeader = "X-SB-Api-Key";

    private static async Task<(WebApplication App, HttpClient Client, SpyStore Store, ThrowingContentCache Cache)>
        BuildHostAsync(DashboardAggregateCache? aggregateCache = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.Configure<BotDetectionOptions>(o =>
        {
            o.ApiKeys[ApiKeyId] = new ApiKeyConfig { Key = ApiKeyId };
        });

        var store = new SpyStore();
        var cache = new ThrowingContentCache();
        builder.Services.AddSingleton<IDashboardEventStore>(store);
        builder.Services.AddSingleton(aggregateCache ?? new DashboardAggregateCache());
        builder.Services.AddSingleton<IDashboardContentCache>(cache);
        builder.Services.AddSingleton<IDashboardPageManifestSource>(new SingleManifestSource());
        builder.Services.AddStyloBotApi();

        var app = builder.Build();
        app.MapReadEndpoints();
        await app.StartAsync();

        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(ApiKeyHeader, ApiKeyId);
        return (app, client, store, cache);
    }

    [Fact]
    public async Task Endpoints_forwards_normalized_domains_audience_and_window_to_the_store()
    {
        var (app, client, store, _) = await BuildHostAsync();
        await using var _app = app;

        // Deliberately NOT 24h: the route must carry the caller's exact pair, not
        // reconstruct a default window from the token.
        var since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 1, 1, 6, 0, 0, DateTimeKind.Utc);
        var url = "/api/v1/endpoints?limit=50"
                  + $"&since={Uri.EscapeDataString(since.ToString("O"))}"
                  + $"&until={Uri.EscapeDataString(until.ToString("O"))}"
                  + "&audience=BOTS&domain=b.example.com&domain=a.example.com&domain=a.example.com";

        var response = await client.GetAsync(url);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "a.example.com", "b.example.com" }, store.LastDomains);
        Assert.Equal("bots", store.LastAudience);
        Assert.Equal(since, store.LastSince);
        Assert.Equal(until, store.LastUntil);
        Assert.Equal("windowed", response.Headers.GetValues("X-StyloBot-Endpoint-Stats-Window").Single());
        Assert.Contains("\"endpointStatsWindow\":\"windowed\"", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Timeseries_scoped_request_bypasses_unscoped_aggregate_snapshot()
    {
        // Seed the aggregate fast path with a sentinel the store would never return.
        var aggregate = new DashboardAggregateCache();
        aggregate.Update(new DashboardAggregateCache.AggregateSnapshot
        {
            Countries = [],
            Endpoints = [],
            UserAgents = [],
            TimeSeries =
            [
                new DashboardTimeSeriesPoint
                {
                    Timestamp = DateTime.UtcNow.AddMinutes(-5),
                    TotalCount = 999,
                    BotCount = 999,
                    HumanCount = 0
                }
            ],
            ComputedAt = DateTime.UtcNow
        });

        var (app, client, store, cache) = await BuildHostAsync(aggregate);
        await using var _app = app;

        // Control: the unscoped default view still takes the cache (sentinel 999).
        var unscoped = await client.GetAsync("/api/v1/timeseries?interval=5m");
        Assert.Equal(HttpStatusCode.OK, unscoped.StatusCode);
        var unscopedBody = await unscoped.Content.ReadAsStringAsync();
        Assert.Contains("999", unscopedBody);
        Assert.Equal(0, store.TimeSeriesCalls);

        // Scoped: must not touch the unscoped snapshot or the warm envelope.
        var scoped = await client.GetAsync("/api/v1/timeseries?interval=5m&domain=a.example.com&audience=bots");
        Assert.Equal(HttpStatusCode.OK, scoped.StatusCode);
        Assert.Equal(1, store.TimeSeriesCalls);
        Assert.Equal(new[] { "a.example.com" }, store.LastDomains);
        Assert.Equal("bots", store.LastAudience);
        var scopedBody = await scoped.Content.ReadAsStringAsync();
        Assert.DoesNotContain("999", scopedBody);
        Assert.Equal(0, cache.GetCurrentCalls);
    }

    [Fact]
    public async Task Endpoint_detail_forwards_normalized_domain_scope()
    {
        var (app, client, store, _) = await BuildHostAsync();
        await using var _app = app;

        var response = await client.GetAsync(
            "/api/v1/endpoints/GET/api/widget?domain=b.example.com&domain=A.Example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "a.example.com", "b.example.com" }, store.LastDetailDomains);
    }

    [Fact]
    public async Task Query_parameter_normalization_matches_the_in_process_traffic_query()
    {
        Assert.Null(DashboardQueryParameters.NormalizeDomains(null));
        Assert.Null(DashboardQueryParameters.NormalizeDomains([" ", ""]));
        Assert.Equal(
            new[] { "a.example.com", "b.example.com" },
            DashboardQueryParameters.NormalizeDomains([" B.Example.COM ", "a.example.com", "A.EXAMPLE.COM"]));
        Assert.Null(DashboardQueryParameters.NormalizeAudience(" "));
        Assert.Equal("bots", DashboardQueryParameters.NormalizeAudience(" BOTS "));
    }

    [Fact]
    public async Task Endpoints_advertises_lifetime_when_the_store_cannot_window()
    {
        var (app, client, store, _) = await BuildHostAsync();
        await using var _app = app;
        store.SupportsWindowedEndpointStats = false;

        var response = await client.GetAsync("/api/v1/endpoints?domain=a.example.com");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("lifetime", response.Headers.GetValues("X-StyloBot-Endpoint-Stats-Window").Single());
        Assert.Contains("\"endpointStatsWindow\":\"lifetime\"", await response.Content.ReadAsStringAsync());
    }

    private sealed class SpyStore : IDashboardEventStore
    {
        public bool SupportsWindowedEndpointStats { get; set; } = true;
        public int TimeSeriesCalls;
        public IReadOnlyList<string>? LastDomains;
        public IReadOnlyList<string>? LastDetailDomains;
        public string? LastAudience;
        public DateTime? LastSince;
        public DateTime? LastUntil;

        public Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
            int count = 50, DateTime? startTime = null, DateTime? endTime = null,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            LastDomains = domains;
            LastAudience = audienceFilter;
            LastSince = startTime;
            LastUntil = endTime;
            return Task.FromResult(new List<DashboardEndpointStats>
            {
                new()
                {
                    Method = "GET",
                    Path = "/api/widget",
                    TotalCount = 7,
                    BotCount = 2,
                    LastSeen = DateTime.UtcNow
                }
            });
        }

        public Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
            DateTime startTime, DateTime endTime, TimeSpan bucketSize,
            string? audienceFilter = null, IReadOnlyList<string>? domains = null)
        {
            TimeSeriesCalls++;
            LastDomains = domains;
            LastAudience = audienceFilter;
            return Task.FromResult(new List<DashboardTimeSeriesPoint>
            {
                new() { Timestamp = startTime, TotalCount = 1, BotCount = 1, HumanCount = 0 }
            });
        }

        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(
            string method, string path, DateTime? startTime = null, DateTime? endTime = null)
            => GetEndpointDetailAsync(method, path, startTime, endTime, domains: null);

        public Task<DashboardEndpointDetail?> GetEndpointDetailAsync(
            string method, string path, DateTime? startTime, DateTime? endTime,
            IReadOnlyList<string>? domains)
        {
            LastDetailDomains = domains;
            return Task.FromResult<DashboardEndpointDetail?>(new DashboardEndpointDetail
            {
                Method = method,
                Path = path,
                TotalCount = 1,
                BotCount = 0,
                TopActions = new Dictionary<string, int>(),
                TopCountries = new Dictionary<string, int>(),
                RiskBands = new Dictionary<string, int>(),
                TopBots = new List<DashboardTopBotEntry>(),
                RecentDetections = new List<SignatureDetectionRow>()
            });
        }

        // ---- unused members ----
        public Task AddDetectionAsync(DashboardDetectionEvent detection) => throw new NotImplementedException();
        public Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature) => throw new NotImplementedException();
        public Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null) => throw new NotImplementedException();
        public Task<DashboardSummary> GetSummaryAsync(DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null) => throw new NotImplementedException();
        public Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(string signature, int topN = 25, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null) => throw new NotImplementedException();
        public Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20) => throw new NotImplementedException();
        public Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(string family, int hours = 168, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordDegradationSnapshotAsync(Mostlylucid.BotDetection.RateLimit.DegradationSnapshot snapshot, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<Mostlylucid.BotDetection.RateLimit.DegradationSnapshot>> GetDegradationHistoryAsync(DateTime startTime, DateTime endTime, CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class ThrowingContentCache : IDashboardContentCache
    {
        public int GetCurrentCalls { get; private set; }

        public Task<DashboardPageResult> GetCurrentAsync(
            DashboardPageManifest manifest, DashboardPageWindow window, CancellationToken ct)
        {
            GetCurrentCalls++;
            throw new InvalidOperationException("scoped read must not use the unscoped warm envelope");
        }

        public Task<DashboardPageResult> GetAsync(
            DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct)
            => throw new InvalidOperationException("not used");

        public Task<DashboardPageResult> WarmAsync(
            DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct)
            => throw new InvalidOperationException("not used");

        public bool TryGet(
            DashboardPageManifest manifest, DashboardPageWindow window, long tick, out DashboardPageResult? result)
        {
            result = null;
            return false;
        }

        public IReadOnlyCollection<(DashboardPageManifest Manifest, DashboardPageWindow Window, int AccessCount, DateTimeOffset LastAccess)> LiveEnvelopes()
            => [];
    }

    private sealed class SingleManifestSource : IDashboardPageManifestSource
    {
        public DashboardPageManifest? For(string pageKey) =>
            pageKey == "dashboard.traffic"
                ? new DashboardPageManifest("dashboard.traffic", ["time-chart"])
                : null;
    }
}
