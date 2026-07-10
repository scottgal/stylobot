using System.Diagnostics;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.AspNetPack.Configuration;
using Mostlylucid.BotDetection.AspNetPack.Logging;
using Xunit;

namespace Mostlylucid.BotDetection.Test.AspNetPack;

/// <summary>
///     Prod incident (stylo.bot 14-21s TTFB on every request): the OTLP log exporter
///     POSTed to an unresolvable collector on a HttpClient with no timeout (default
///     100s), so each export hung on the DNS stall (~14-21s), jammed the drainer, and
///     coupled into the request path. Telemetry is best-effort — a dead collector must
///     NEVER be able to add latency. This asserts the export is bounded by
///     <see cref="LogSinkOptions.ExportTimeout"/>, not by the stall.
/// </summary>
public sealed class LogSinkExportTimeoutTests
{
    /// <summary>Simulates an unreachable/stalled collector: never responds within the test window.</summary>
    private sealed class HangingHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(60), ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }

    private static LogRecord SampleRecord() => new(
        TimestampUtc: DateTime.UtcNow,
        Level: LogLevel.Information,
        Category: "Test",
        Body: "hello",
        TraceIdHex: null,
        SpanIdHex: null,
        FingerprintId: null,
        Attributes: new Dictionary<string, string>());

    [Fact]
    public async Task ExportAsync_AgainstStalledCollector_IsBoundedByTimeout_NotTheStall()
    {
        // The named HttpClient is registered with Timeout = LogSinkOptions.ExportTimeout;
        // here we apply the same 1s bound directly and point at an unreachable host.
        using var http = new HttpClient(new HangingHandler()) { Timeout = TimeSpan.FromSeconds(1) };
        var opts = Options.Create(new LogSinkOptions
        {
            GatewayEndpoint = "http://unreachable-collector.invalid:4318",
            ExportTimeout = TimeSpan.FromSeconds(1)
        });
        var exporter = new StyloBotGatewayLogExporter(
            http, opts, NullLogger<StyloBotGatewayLogExporter>.Instance);

        var sw = Stopwatch.StartNew();
        var result = await exporter.ExportAsync(new[] { SampleRecord() }, CancellationToken.None);
        sw.Stop();

        // The 1s timeout must fire long before the 60s handler hang. Generous ceiling
        // for CI scheduling jitter, but far below the old ~14-21s (let alone 60s) stall.
        sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10),
            "a stalled collector must be bounded by ExportTimeout, never hang the drainer for the full stall");
        result.Outcome.Should().NotBe(ExportOutcome.Success,
            "the export failed fast; it must not report success");
    }

    [Fact]
    public void ExportTimeout_DefaultsTo_AFewSeconds_NotTheHttpClientDefault()
    {
        // Guards the fix: the default must be a small bound, never the 100s HttpClient
        // default that let the DNS stall through.
        new LogSinkOptions().ExportTimeout.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(10));
        new LogSinkOptions().ExportTimeout.Should().BeGreaterThan(TimeSpan.Zero);
    }
}
