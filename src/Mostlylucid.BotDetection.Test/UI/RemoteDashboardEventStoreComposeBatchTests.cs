using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.UI.Adapters.Remote;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Unit tests for <see cref="RemoteDashboardEventStore.ComposeBatchAsync"/>:
///     verifies it issues exactly one POST to <c>/api/v1/compose-batch</c> and
///     deserializes the returned <see cref="DashboardDatasetBundle"/> correctly.
///     Uses a stub <see cref="HttpMessageHandler"/> so no real gateway is needed.
/// </summary>
public sealed class RemoteDashboardEventStoreComposeBatchTests
{
    private static (IDashboardEventStore Store, CapturingHandler Handler) BuildStore(
        DashboardDatasetBundle? bundleToReturn)
    {
        // Wrap the bundle in the gateway's SingleResponse<T> envelope shape
        // (the gateway returns { "data": <bundle> }; RemoteEnvelope reads "data").
        var envelopeJson = bundleToReturn is null
            ? """{"data":null}"""
            : $$"""{"data":{{JsonSerializer.Serialize(bundleToReturn)}}}""";

        var handler = new CapturingHandler(HttpStatusCode.OK, envelopeJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        var api = new GatewayApiClient(http, NullLogger<GatewayApiClient>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        IDashboardEventStore store = new RemoteDashboardEventStore(api, cache);
        return (store, handler);
    }

    [Fact]
    public async Task ComposeBatch_POSTs_to_compose_batch_path()
    {
        var expected = new DashboardDatasetBundle(
            Summary: new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 42,
                BotRequests = 5,
                HumanRequests = 37,
                UncertainRequests = 0,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>(),
                UniqueSignatures = 3
            },
            TimeBuckets: null,
            BotAggregate: null,
            Geo: null,
            Endpoints: null);

        var (store, handler) = BuildStore(expected);

        var req = new DashboardBatchRequest(
            StartTime: null, EndTime: null,
            Datasets: [new DatasetRequest(DatasetKind.SummaryStats)]);

        var bundle = await store.ComposeBatchAsync(req, default);

        // Exactly ONE round-trip (not N per widget)
        Assert.Equal(1, handler.CallCount);

        // Correct method + path
        Assert.Equal(HttpMethod.Post, handler.LastMethod);
        Assert.Equal("/api/v1/compose-batch", handler.LastPath);

        // Payload deserialized correctly
        Assert.NotNull(bundle.Summary);
        Assert.Equal(42, bundle.Summary!.TotalRequests);
        Assert.Equal(5, bundle.Summary.BotRequests);
        Assert.Null(bundle.BotAggregate);
        Assert.Null(bundle.Geo);
    }

    [Fact]
    public async Task ComposeBatch_sends_request_body_as_json()
    {
        var (store, handler) = BuildStore(new DashboardDatasetBundle(null, null, null, null, null));

        var req = new DashboardBatchRequest(
            StartTime: new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            EndTime: new DateTime(2025, 1, 2, 0, 0, 0, DateTimeKind.Utc),
            Datasets:
            [
                new DatasetRequest(DatasetKind.BotAggregate, TopN: 25),
                new DatasetRequest(DatasetKind.GeoBreakdown, TopN: 10)
            ]);

        await store.ComposeBatchAsync(req, default);

        Assert.NotNull(handler.LastBody);
        // Body must be valid JSON. System.Text.Json defaults to camelCase property names
        // (PostAsJsonAsync uses the shared web defaults), so the property may be "datasets"
        // or "Datasets" depending on the serializer options in effect.  Accept either form.
        var doc = JsonDocument.Parse(handler.LastBody!);
        var found = doc.RootElement.TryGetProperty("datasets", out var datasets)
                    || doc.RootElement.TryGetProperty("Datasets", out datasets);
        Assert.True(found, "Expected 'datasets' or 'Datasets' property in POST body");
        Assert.Equal(2, datasets.GetArrayLength());
    }

    [Fact]
    public async Task ComposeBatch_returns_empty_bundle_on_non_success()
    {
        // Gateway returns 500 — store should degrade gracefully, not throw
        var handler = new CapturingHandler(HttpStatusCode.InternalServerError, "oops");
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        var api = new GatewayApiClient(http, NullLogger<GatewayApiClient>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        IDashboardEventStore store = new RemoteDashboardEventStore(api, cache);

        var req = new DashboardBatchRequest(
            StartTime: null, EndTime: null,
            Datasets: [new DatasetRequest(DatasetKind.SummaryStats)]);

        var bundle = await store.ComposeBatchAsync(req, default);

        // Graceful empty bundle, no throw
        Assert.Null(bundle.Summary);
        Assert.Null(bundle.BotAggregate);
    }

    [Fact]
    public async Task Windowed_topbots_falls_back_to_populated_live_aggregate()
    {
        var live = new DashboardTopBotEntry
        {
            PrimarySignature = "visitor-1",
            HitCount = 7,
            BotName = "Human",
            LastSeen = DateTime.UtcNow,
        };
        var handler = new WindowedTopBotsHandler(live);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        var api = new GatewayApiClient(http, NullLogger<GatewayApiClient>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        IDashboardEventStore store = new RemoteDashboardEventStore(api, cache);

        var rows = await store.GetTopBotsAsync(
            count: 500,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");

        Assert.Single(rows);
        Assert.Equal("visitor-1", rows[0].PrimarySignature);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains(handler.Paths, p => p.Contains("since=", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, p => !p.Contains("since=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windowed_endpoint_stats_falls_back_to_populated_live_aggregate()
    {
        var live = new DashboardEndpointStats
        {
            Method = "GET",
            Path = "/",
            TotalCount = 4545,
            LastSeen = DateTime.UtcNow,
        };
        var handler = new WindowedEndpointStatsHandler(live);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        var api = new GatewayApiClient(http, NullLogger<GatewayApiClient>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        IDashboardEventStore store = new RemoteDashboardEventStore(api, cache);

        var rows = await store.GetEndpointStatsAsync(
            count: 500,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");

        Assert.Single(rows);
        Assert.Equal("/", rows[0].Path);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains(handler.Paths, p => p.Contains("since=", StringComparison.Ordinal));
        Assert.Contains(handler.Paths, p => !p.Contains("since=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Windowed_endpoint_stats_stays_empty_when_live_aggregate_is_also_empty()
    {
        var handler = new WindowedEndpointStatsHandler(live: null);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        var api = new GatewayApiClient(http, NullLogger<GatewayApiClient>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        IDashboardEventStore store = new RemoteDashboardEventStore(api, cache);

        var rows = await store.GetEndpointStatsAsync(
            count: 500,
            startTime: DateTime.UtcNow.AddHours(-24),
            endTime: DateTime.UtcNow,
            audienceFilter: "all");

        Assert.Empty(rows);
        Assert.Equal(2, handler.CallCount);
    }

    private sealed class WindowedEndpointStatsHandler(DashboardEndpointStats? live) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            Paths.Add(path);
            var body = path.Contains("since=", StringComparison.Ordinal) || live is null
                ? "{\"data\":[],\"pagination\":{\"offset\":0,\"limit\":500,\"total\":0},\"meta\":{}}"
                : JsonSerializer.Serialize(new
                {
                    data = new[] { live },
                    pagination = new { offset = 0, limit = 500, total = 1 },
                    meta = new { }
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class WindowedTopBotsHandler(DashboardTopBotEntry live) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            Paths.Add(path);
            var body = path.Contains("since=", StringComparison.Ordinal)
                ? "{\"data\":[],\"pagination\":{\"offset\":0,\"limit\":500,\"total\":0},\"meta\":{}}"
                : JsonSerializer.Serialize(new
                {
                    data = new[] { live },
                    pagination = new { offset = 0, limit = 500, total = 1 },
                    meta = new { }
                });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public HttpMethod? LastMethod { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastMethod = request.Method;
            LastPath = request.RequestUri?.AbsolutePath;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(ct);

            return new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }
    }
}
