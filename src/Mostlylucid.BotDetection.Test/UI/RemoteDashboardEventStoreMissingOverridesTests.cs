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
///     Regression tests for the DIM (default-interface-member) gap on
///     <see cref="RemoteDashboardEventStore"/>: four <see cref="IDashboardEventStore"/>
///     methods (<see cref="IDashboardEventStore.GetSignatureTimeSeriesAsync"/>,
///     <see cref="IDashboardEventStore.GetLiveTimeSeriesAsync"/>,
///     <see cref="IDashboardEventStore.GetLiveEndpointStatsAsync"/>,
///     <see cref="IDashboardEventStore.TryGetSignatureAsync"/>) have default
///     implementations on the interface that return an empty list / null
///     unconditionally. Before this store overrides them, calling any of these
///     four through the <see cref="IDashboardEventStore"/> reference silently
///     falls through to that default -- the stubbed gateway response below is
///     never even requested (<c>Handler.CallCount</c> stays 0) -- no exception,
///     no log line, just empty/null data on the remote-viewer dashboard.
///
///     Each test asserts on SPECIFIC field values from the stub, not just
///     non-null/non-empty, so a future implementation that reaches the gateway
///     but decodes the wrong shape still fails here.
/// </summary>
public sealed class RemoteDashboardEventStoreMissingOverridesTests
{
    private static (IDashboardEventStore Store, CapturingHandler Handler) BuildStore(string envelopeJson)
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, envelopeJson);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://gw.test/") };
        var api = new GatewayApiClient(http, NullLogger<GatewayApiClient>.Instance);
        var cache = new MemoryCache(new MemoryCacheOptions());
        IDashboardEventStore store = new RemoteDashboardEventStore(api, cache);
        return (store, handler);
    }

    [Fact]
    public async Task GetSignatureTimeSeriesAsync_returns_stubbed_points()
    {
        var timestamp = new DateTime(2026, 8, 22, 10, 0, 0, DateTimeKind.Utc);
        var envelopeJson = "{\"data\":[{\"Timestamp\":\"" + timestamp.ToString("o") +
            "\",\"TotalCount\":42,\"AvgBotProbability\":0.87,\"AvgConfidence\":0.91,\"AvgProcessingTimeMs\":12.5}]}";
        var (store, handler) = BuildStore(envelopeJson);

        var result = await store.GetSignatureTimeSeriesAsync(
            "sig-abc",
            timestamp.AddHours(-1),
            timestamp,
            TimeSpan.FromMinutes(5));

        Assert.Equal(1, handler.CallCount);
        Assert.Contains("/api/v1/signatures/sig-abc/timeseries", handler.LastPath);
        Assert.Single(result);
        Assert.Equal(42, result[0].TotalCount);
        Assert.Equal(0.87, result[0].AvgBotProbability);
        Assert.Equal(0.91, result[0].AvgConfidence);
        Assert.Equal(12.5, result[0].AvgProcessingTimeMs);
    }

    [Fact]
    public async Task GetLiveTimeSeriesAsync_returns_stubbed_points()
    {
        var timestamp = new DateTime(2026, 8, 22, 11, 0, 0, DateTimeKind.Utc);
        var envelopeJson = "{\"data\":[{\"Timestamp\":\"" + timestamp.ToString("o") +
            "\",\"BotCount\":3,\"HumanCount\":7,\"TotalCount\":10,\"BytesOut\":1000,\"AvgProcessingTimeMs\":5.5,\"P95ProcessingTimeMs\":9.9,\"MaxProcessingTimeMs\":15.0}]}";
        var (store, handler) = BuildStore(envelopeJson);

        var result = await store.GetLiveTimeSeriesAsync(
            timestamp.AddMinutes(-30),
            timestamp,
            TimeSpan.FromMinutes(1),
            audienceFilter: "bots");

        Assert.Equal(1, handler.CallCount);
        Assert.Contains("/api/v1/timeseries/live", handler.LastPath);
        Assert.Single(result);
        Assert.Equal(3, result[0].BotCount);
        Assert.Equal(7, result[0].HumanCount);
        Assert.Equal(10, result[0].TotalCount);
        Assert.Equal(1000, result[0].BytesOut);
    }

    [Fact]
    public async Task GetLiveEndpointStatsAsync_returns_stubbed_stats()
    {
        var lastSeen = new DateTime(2026, 8, 22, 12, 0, 0, DateTimeKind.Utc);
        var envelopeJson = "{\"data\":[{\"Method\":\"GET\",\"Path\":\"/api/foo\",\"TotalCount\":55,\"BotCount\":10,\"BytesOut\":2048,\"LastSeen\":\"" +
            lastSeen.ToString("o") + "\"}]}";
        var (store, handler) = BuildStore(envelopeJson);

        var result = await store.GetLiveEndpointStatsAsync(
            lastSeen.AddHours(-1),
            lastSeen,
            audienceFilter: "all");

        Assert.Equal(1, handler.CallCount);
        Assert.Contains("/api/v1/endpoints/live", handler.LastPath);
        Assert.Single(result);
        Assert.Equal("GET", result[0].Method);
        Assert.Equal("/api/foo", result[0].Path);
        Assert.Equal(55, result[0].TotalCount);
        Assert.Equal(10, result[0].BotCount);
    }

    [Fact]
    public async Task TryGetSignatureAsync_returns_stubbed_signature()
    {
        var timestamp = new DateTime(2026, 8, 22, 13, 0, 0, DateTimeKind.Utc);
        var envelopeJson = "{\"data\":{\"SignatureId\":\"sig-xyz\",\"Timestamp\":\"" + timestamp.ToString("o") +
            "\",\"PrimarySignature\":\"sig-xyz\",\"RiskBand\":\"high\",\"HitCount\":99,\"IsKnownBot\":true,\"BotName\":\"Testbot\"}}";
        var (store, handler) = BuildStore(envelopeJson);

        var result = await store.TryGetSignatureAsync("sig-xyz");

        Assert.Equal(1, handler.CallCount);
        Assert.Contains("/api/v1/signatures/sig-xyz", handler.LastPath);
        Assert.NotNull(result);
        Assert.Equal("sig-xyz", result!.SignatureId);
        Assert.Equal(99, result.HitCount);
        Assert.True(result.IsKnownBot);
        Assert.Equal("Testbot", result.BotName);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int CallCount { get; private set; }
        public string? LastPath { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastPath = request.RequestUri?.PathAndQuery;

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            });
        }
    }
}
