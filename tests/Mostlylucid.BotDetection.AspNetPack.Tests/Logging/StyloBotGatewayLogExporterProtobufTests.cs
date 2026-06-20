using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.AspNetPack.Configuration;
using Mostlylucid.BotDetection.AspNetPack.Logging;
using OpenTelemetry.Proto.Collector.Logs.V1;
using Xunit;

namespace Mostlylucid.BotDetection.AspNetPack.Tests.Logging;

public class StyloBotGatewayLogExporterProtobufTests
{
    [Fact]
    public async Task Export_posts_application_x_protobuf_decodable_as_ExportLogsServiceRequest()
    {
        var captured = new CapturingHandler();
        var http = new HttpClient(captured) { BaseAddress = new Uri("http://gateway:4318") };
        var opts = Options.Create(new LogSinkOptions
        {
            GatewayEndpoint = "http://gateway:4318",
        });
        var exporter = new StyloBotGatewayLogExporter(http, opts, NullLogger<StyloBotGatewayLogExporter>.Instance);

        var batch = new List<LogRecord>
        {
            new(
                TimestampUtc: DateTime.UtcNow,
                Level: LogLevel.Information,
                Category: "Test",
                Body: "hello",
                TraceIdHex: null,
                SpanIdHex: null,
                FingerprintId: "fp_abc",
                Attributes: new Dictionary<string, string>()),
        };

        var result = await exporter.ExportAsync(batch, CancellationToken.None);

        result.Outcome.Should().Be(ExportOutcome.Success);
        captured.LastContentType.Should().Be("application/x-protobuf");
        var parsed = ExportLogsServiceRequest.Parser.ParseFrom(captured.LastBody);
        parsed.ResourceLogs.Should().NotBeEmpty();
        var first = parsed.ResourceLogs[0];
        first.Resource.Attributes
            .Should().Contain(a => a.Key == "sb-fp" && a.Value.StringValue == "fp_abc");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? LastContentType;
        public byte[] LastBody = Array.Empty<byte>();
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
        {
            LastContentType = req.Content?.Headers.ContentType?.MediaType;
            LastBody = req.Content is not null ? await req.Content.ReadAsByteArrayAsync(ct) : Array.Empty<byte>();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        }
    }
}
