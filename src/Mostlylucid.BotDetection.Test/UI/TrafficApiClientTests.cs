using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Dashboard.Contracts;
using Mostlylucid.BotDetection.UI.Services.Traffic;

namespace Mostlylucid.BotDetection.Test.UI;

public sealed class TrafficApiClientTests
{
    [Theory]
    [InlineData(400, TrafficReadState.InvalidQuery)]
    [InlineData(401, TrafficReadState.AccessDenied)]
    [InlineData(403, TrafficReadState.AccessDenied)]
    [InlineData(503, TrafficReadState.Unavailable)]
    public async Task Http_failures_never_become_empty_data(int status, TrafficReadState expected)
    {
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)));
        var result = await Read(http);
        result.State.Should().Be(expected);
        result.Snapshot.Should().BeNull();
        result.TraceId.Should().Be("local-reference");
    }

    [Theory]
    [InlineData("{\"generatedAtUtc\":\"2026-10-17T00:00:00Z\",\"fromUtc\":\"2026-10-16T00:00:00Z\",\"toUtc\":\"2026-10-17T00:00:00Z\",\"window\":\"24h\",\"domains\":[],\"includeInternal\":false,\"summary\":{},\"timeline\":[],\"detections\":[]}")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{invalid")]
    [InlineData("{\"summary\":{\"requests\":0}}")]
    public async Task Malformed_or_incomplete_payload_is_unavailable(string payload)
    {
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(payload) }));
        var result = await Read(http);
        result.State.Should().Be(TrafficReadState.Unavailable);
        result.Snapshot.Should().BeNull();
    }

    [Theory]
    [InlineData(0, TrafficReadState.Empty)]
    [InlineData(2, TrafficReadState.Ready)]
    public async Task Honest_empty_data_is_distinct_from_failure(int count, TrafficReadState expected)
    {
        var now = DateTimeOffset.UtcNow;
        var snapshot = new TrafficSnapshot(now, now.AddHours(-1), now, "1h", [], false, new(count, count, 0, 0), [], []);
        using var http = Client((request, _) =>
        {
            request.RequestUri!.Query.Should().Contain("domain=one.test").And.Contain("domain=two.test");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = JsonContent.Create(snapshot, DashboardJsonContext.Default.TrafficSnapshot) });
        });
        var result = await new TrafficApiClient(http, NullLogger<TrafficApiClient>.Instance)
            .ReadAsync(new("1h", Domains: ["one.test", "two.test"]), "local-reference", default);
        result.State.Should().Be(expected);
        result.Snapshot.Should().BeEquivalentTo(snapshot);
    }

    [Fact]
    public async Task Transport_timeout_is_unavailable_but_callers_cancellation_is_propagated()
    {
        using var http = Client(async (_, ct) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return new HttpResponseMessage();
        });
        http.Timeout = TimeSpan.FromMilliseconds(50);
        (await Read(http)).State.Should().Be(TrafficReadState.Unavailable);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new TrafficApiClient(http, NullLogger<TrafficApiClient>.Instance).ReadAsync(new(), "cancelled", cancelled.Token));
    }

    [Fact]
    public async Task Timeout_also_bounds_reading_a_stalled_response_body()
    {
        using var http = Client((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledBody() }));
        http.Timeout = TimeSpan.FromMilliseconds(50);
        (await Read(http).WaitAsync(TimeSpan.FromSeconds(2))).State.Should().Be(TrafficReadState.Unavailable);
    }

    private sealed class StalledBody : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken ct)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            return Stream.Null;
        }
    }

    private static Task<TrafficReadResult> Read(HttpClient http) =>
        new TrafficApiClient(http, NullLogger<TrafficApiClient>.Instance).ReadAsync(new(), "local-reference", default);
    private static HttpClient Client(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) =>
        new(new Handler(send)) { BaseAddress = new Uri("http://backend.test/") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
    }
}
