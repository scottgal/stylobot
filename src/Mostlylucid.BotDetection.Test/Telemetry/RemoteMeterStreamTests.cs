using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;

namespace Mostlylucid.BotDetection.Test.Telemetry;

public class RemoteMeterStreamTests
{
    [Fact]
    public async Task Poll_populates_catalog_and_atoms()
    {
        var handler = new CannedResponseHandler
        {
            Body = """
# HELP test_requests_total Test counter.
# TYPE test_requests_total counter
test_requests_total{path="/a"} 100
""",
        };

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "http://gw.test:8080",
        }, handler);

        stream.PumpPollForTesting();

        var catalog = await stream.ListAsync(CancellationToken.None);
        Assert.Contains(catalog, c => c.Name == "test_requests_total" && c.Kind == MeterKind.Counter);

        var ts = await stream.GetAsync("test_requests_total", TimeSpan.FromMinutes(1), buckets: 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(MeterKind.Counter, ts!.Kind);
        Assert.Equal(100d, ts.Current);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task Counter_deltas_across_polls()
    {
        var handler = new CannedResponseHandler();

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "http://gw.test:8080",
        }, handler);

        handler.Body = """
# TYPE deltacount counter
deltacount 100
""";
        stream.PumpPollForTesting();

        handler.Body = """
# TYPE deltacount counter
deltacount 150
""";
        stream.PumpPollForTesting();

        var ts = await stream.GetAsync("deltacount", TimeSpan.FromMinutes(1), buckets: 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(MeterKind.Counter, ts!.Kind);
        // First poll: initial delta=100. Second poll: delta=50.
        // The atom's Counter path adds the per-poll delta to its running
        // cumulative, so Current after two polls = 100 + 50 = 150.
        Assert.Equal(150d, ts.Current);
        // Sum of bucket values reflects the total deltas accumulated (150).
        Assert.Equal(150d, ts.Values.Sum(), precision: 0);
    }

    [Fact]
    public async Task Gauge_uses_absolute_value()
    {
        var handler = new CannedResponseHandler();

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "http://gw.test:8080",
        }, handler);

        handler.Body = """
# TYPE depth gauge
depth 10
""";
        stream.PumpPollForTesting();

        handler.Body = """
# TYPE depth gauge
depth 20
""";
        stream.PumpPollForTesting();

        var ts = await stream.GetAsync("depth", TimeSpan.FromMinutes(1), buckets: 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(MeterKind.Gauge, ts!.Kind);
        Assert.Equal(20d, ts.Current);
        // The per-bucket aggregate for a gauge is the arithmetic mean of
        // observations. We pushed 10 then 20 within (likely) the same wall
        // bucket, so the mean is 15.
        var nonZero = ts.Values.Where(v => v > 0).ToList();
        Assert.NotEmpty(nonZero);
        Assert.InRange(nonZero.Average(), 14.5d, 15.5d);
    }

    [Fact]
    public async Task Histogram_percentiles_are_emitted()
    {
        var handler = new CannedResponseHandler();

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "http://gw.test:8080",
        }, handler);

        handler.Body = """
# TYPE latency_seconds histogram
latency_seconds_bucket{le="0.1"} 50
latency_seconds_bucket{le="1"} 80
latency_seconds_bucket{le="+Inf"} 100
latency_seconds_sum 12.5
latency_seconds_count 100
""";
        stream.PumpPollForTesting();

        handler.Body = """
# TYPE latency_seconds histogram
latency_seconds_bucket{le="0.1"} 60
latency_seconds_bucket{le="1"} 90
latency_seconds_bucket{le="+Inf"} 120
latency_seconds_sum 17.4
latency_seconds_count 120
""";
        stream.PumpPollForTesting();

        var ts = await stream.GetAsync("latency_seconds", TimeSpan.FromMinutes(1), buckets: 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(MeterKind.Histogram, ts!.Kind);
        Assert.NotNull(ts.P50);
        Assert.NotNull(ts.P99);
    }

    [Fact]
    public async Task Meter_name_prefix_filter_is_respected()
    {
        var handler = new CannedResponseHandler
        {
            Body = """
# TYPE keep_a counter
keep_a 1
# TYPE drop_b counter
drop_b 1
""",
        };

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "http://gw.test:8080",
            MeterNamePrefixFilter = "keep_",
        }, handler);

        stream.PumpPollForTesting();

        var catalog = await stream.ListAsync(CancellationToken.None);
        Assert.Single(catalog);
        Assert.Equal("keep_a", catalog[0].Name);
    }

    [Fact]
    public async Task Empty_base_url_makes_stream_inert()
    {
        var handler = new CannedResponseHandler { Body = "# TYPE x counter\nx 1\n" };

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "", // inert
        }, handler);

        // Even with a forced pump (test hook), the inert stream's poll path
        // never hits the handler because BaseUrl is empty.
        stream.PumpPollForTesting();

        var catalog = await stream.ListAsync(CancellationToken.None);
        Assert.Empty(catalog);

        var ts = await stream.GetAsync("x", TimeSpan.FromMinutes(1), 6, CancellationToken.None);
        Assert.Null(ts);

        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task Http_500_preserves_existing_atoms()
    {
        var handler = new CannedResponseHandler();

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "http://gw.test:8080",
        }, handler);

        handler.Body = """
# TYPE persist counter
persist 200
""";
        handler.Status = HttpStatusCode.OK;
        stream.PumpPollForTesting();

        // Confirm we have an atom for "persist".
        var firstCatalog = await stream.ListAsync(CancellationToken.None);
        Assert.Contains(firstCatalog, c => c.Name == "persist");

        // Gateway flaps to 500. Atom must survive.
        handler.Status = HttpStatusCode.InternalServerError;
        handler.Body = "";
        stream.PumpPollForTesting();

        var afterFailureCatalog = await stream.ListAsync(CancellationToken.None);
        Assert.Contains(afterFailureCatalog, c => c.Name == "persist");

        var ts = await stream.GetAsync("persist", TimeSpan.FromMinutes(1), 6, CancellationToken.None);
        Assert.NotNull(ts);
        Assert.Equal(200d, ts!.Current);
    }

    [Fact]
    public async Task Auth_header_sent_when_api_key_set()
    {
        var handler = new CannedResponseHandler
        {
            Body = "# TYPE auth_test counter\nauth_test 1\n",
        };

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "http://gw.test:8080",
            ApiKey = "secret-token-xyz",
        }, handler);

        stream.PumpPollForTesting();

        Assert.NotNull(handler.LastAuthorization);
        Assert.Equal("Bearer", handler.LastAuthorization!.Scheme);
        Assert.Equal("secret-token-xyz", handler.LastAuthorization.Parameter);
    }

    [Fact]
    public async Task Auth_header_omitted_when_no_api_key()
    {
        var handler = new CannedResponseHandler
        {
            Body = "# TYPE noauth counter\nnoauth 1\n",
        };

        await using var stream = await StartStreamAsync(new RemoteMeterStreamOptions
        {
            BaseUrl = "http://gw.test:8080",
        }, handler);

        stream.PumpPollForTesting();

        Assert.Null(handler.LastAuthorization);
    }

    // ---- helpers ----

    private static async Task<RemoteMeterStream> StartStreamAsync(
        RemoteMeterStreamOptions opts,
        HttpMessageHandler handler)
    {
        // Force the background PeriodicTimer to a cadence far longer than any
        // test will ever live for. Combined with the removal of the boot poll
        // inside StartAsync, every poll in these tests is driven explicitly by
        // PumpPollForTesting -- no race with timer-driven scrapes.
        opts.PollInterval = TimeSpan.FromHours(1);
        opts.PollTimeout = TimeSpan.FromSeconds(4);

        var stream = new RemoteMeterStream(
            Options.Create(opts),
            NullLogger<RemoteMeterStream>.Instance,
            signalSink: null,
            httpClientFactory: null,
            injectedHandler: handler);
        await stream.StartAsync(CancellationToken.None);
        return stream;
    }

    private sealed class CannedResponseHandler : HttpMessageHandler
    {
        public string Body { get; set; } = "";
        public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
        public int CallCount { get; private set; }
        public AuthenticationHeaderValue? LastAuthorization { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            CallCount++;
            LastAuthorization = request.Headers.Authorization;
            var resp = new HttpResponseMessage(Status)
            {
                Content = new StringContent(Body, Encoding.UTF8, "text/plain"),
            };
            return Task.FromResult(resp);
        }
    }
}
