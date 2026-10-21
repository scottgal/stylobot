using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     The remote website sends domain/audience on its dashboard reads. These tests pin
///     that the adapter actually puts them on the wire for endpoints, timeseries and
///     endpoint detail — the gateway-side contract is covered by
///     ReadEndpointsQueryScopeTests.
/// </summary>
public sealed class RemoteDashboardEventStoreScopeTests
{
    private static (IDashboardEventStore Store, CapturingHandler Handler) BuildStore(string? endpointStatsWindow = null)
    {
        var handler = new CapturingHandler { EndpointStatsWindow = endpointStatsWindow };
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        var api = new GatewayApiClient(http, NullLogger<GatewayApiClient>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        IDashboardEventStore store = new RemoteDashboardEventStore(api, cache);
        return (store, handler);
    }

    [Fact]
    public async Task Endpoint_stats_forwards_domains_audience_and_window()
    {
        var (store, handler) = BuildStore();
        var since = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var until = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc);

        await store.GetEndpointStatsAsync(500, since, until, "bots", new[] { "a.example.com", "b.example.com" });

        var url = handler.LastUrl!;
        Assert.Contains("audience=bots", url);
        Assert.Contains("domain=a.example.com", url);
        Assert.Contains("domain=b.example.com", url);
        Assert.Contains("since=", url);
        Assert.Contains("until=", url);
    }

    [Fact]
    public async Task Timeseries_forwards_domains_and_audience()
    {
        var (store, handler) = BuildStore();

        await store.GetTimeSeriesAsync(
            DateTime.UtcNow.AddHours(-24), DateTime.UtcNow, TimeSpan.FromMinutes(5),
            "humans", new[] { "a.example.com" });

        var url = handler.LastUrl!;
        Assert.Contains("audience=humans", url);
        Assert.Contains("domain=a.example.com", url);
        Assert.Contains("interval=5m", url);
    }

    [Fact]
    public async Task Endpoint_detail_forwards_domain_scope()
    {
        var (store, handler) = BuildStore();

        var detail = await store.GetEndpointDetailAsync(
            "GET", "/api/widget", domains: new[] { "a.example.com", "b.example.com" });

        Assert.NotNull(detail);
        var url = handler.LastUrl!;
        Assert.Contains("/api/v1/endpoints/GET/api/widget", url);
        Assert.Contains("domain=a.example.com", url);
        Assert.Contains("domain=b.example.com", url);
    }

    [Fact]
    public async Task Endpoint_stats_advertises_windowed_only_when_gateway_says_so()
    {
        var (store, handler) = BuildStore(endpointStatsWindow: "lifetime");
        Assert.False(store.SupportsWindowedEndpointStats);

        await store.GetEndpointStatsAsync(500, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);
        Assert.False(store.SupportsWindowedEndpointStats);

        handler.EndpointStatsWindow = "windowed";
        // Distinct scope so the render-burst cache cannot answer the second call.
        await store.GetEndpointStatsAsync(
            500, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow,
            domains: new[] { "b.example.com" });
        Assert.True(store.SupportsWindowedEndpointStats);
    }

    [Fact]
    public async Task Endpoint_stats_remains_conservative_when_gateway_omits_capability()
    {
        var (store, _) = BuildStore(endpointStatsWindow: null);

        await store.GetEndpointStatsAsync(500, DateTime.UtcNow.AddHours(-1), DateTime.UtcNow);

        Assert.False(store.SupportsWindowedEndpointStats);
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? EndpointStatsWindow { get; set; }
        public string? LastUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            LastUrl = request.RequestUri?.PathAndQuery ?? string.Empty;
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            string body;
            if (path.Contains("/endpoints/", StringComparison.Ordinal))
            {
                var detail = new DashboardEndpointDetail
                {
                    Method = "GET",
                    Path = "/api/widget",
                    TotalCount = 1,
                    BotCount = 0,
                    TopActions = new Dictionary<string, int>(),
                    TopCountries = new Dictionary<string, int>(),
                    RiskBands = new Dictionary<string, int>(),
                    TopBots = new List<DashboardTopBotEntry>(),
                    RecentDetections = new List<SignatureDetectionRow>()
                };
                body = "{\"data\":" + JsonSerializer.Serialize(detail) + ",\"meta\":{}}";
            }
            else
            {
                var meta = EndpointStatsWindow is null
                    ? "{}"
                    : JsonSerializer.Serialize(new { endpointStatsWindow = EndpointStatsWindow });
                body = "{\"data\":[],\"pagination\":{\"offset\":0,\"limit\":500,\"total\":0},\"meta\":" + meta + "}";
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
