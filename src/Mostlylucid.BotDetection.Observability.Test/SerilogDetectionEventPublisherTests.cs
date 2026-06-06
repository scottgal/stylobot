using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Observability.Events;
using Mostlylucid.BotDetection.Orchestration.Telemetry;
using Serilog;
using Serilog.Events;
using Serilog.Extensions.Logging;
using Serilog.Sinks.InMemory;

namespace Mostlylucid.BotDetection.Observability.Test;

public class SerilogDetectionEventPublisherTests
{
    private (SerilogDetectionEventPublisher publisher, InMemorySink sink) Build()
    {
        var sink = new InMemorySink();
        var serilog = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.Sink(sink)
            .CreateLogger();
        var factory = new SerilogLoggerFactory(serilog);
        var logger = factory.CreateLogger<SerilogDetectionEventPublisher>();
        return (new SerilogDetectionEventPublisher(logger), sink);
    }

    [Fact]
    public async Task Bot_block_event_is_logged_at_Warning_with_all_properties()
    {
        var (publisher, sink) = Build();
        var evt = new DetectionEvent
        {
            Timestamp = DateTime.UtcNow,
            RequestId = "req-1",
            Signature = "sig-abc",
            Path = "/wp-login.php",
            Method = "GET",
            StatusCode = 403,
            IsBot = true,
            BotProbability = 0.97,
            Confidence = 0.91,
            RiskBand = "high",
            ThreatBand = "high",
            Action = "block",
            BotName = "wp-scanner",
            BotType = "Scanner",
            CountryCode = "RU",
            ProcessingTimeMs = 4.2
        };

        await publisher.PublishAsync(evt);

        var entry = sink.LogEvents.Should().ContainSingle().Subject;
        entry.Level.Should().Be(LogEventLevel.Warning);
        entry.Properties.Should().ContainKey("StyloBot_Signature");
        entry.Properties["StyloBot_Signature"].ToString().Should().Contain("sig-abc");
        entry.Properties["StyloBot_IsBot"].ToString().Should().Be("True");
        entry.Properties["StyloBot_Action"].ToString().Should().Contain("block");
    }

    [Fact]
    public async Task Human_allow_event_is_logged_at_Debug()
    {
        var (publisher, sink) = Build();
        var evt = new DetectionEvent
        {
            Timestamp = DateTime.UtcNow,
            RequestId = "req-2",
            Signature = "sig-h",
            IsBot = false,
            BotProbability = 0.04,
            Action = "allow"
        };

        await publisher.PublishAsync(evt);

        sink.LogEvents.Should().ContainSingle().Which.Level.Should().Be(LogEventLevel.Debug);
    }

    [Fact]
    public async Task Challenge_event_is_logged_at_Information()
    {
        var (publisher, sink) = Build();
        var evt = new DetectionEvent
        {
            Timestamp = DateTime.UtcNow,
            RequestId = "req-3",
            Signature = "sig-c",
            IsBot = true,
            Action = "challenge"
        };

        await publisher.PublishAsync(evt);

        sink.LogEvents.Should().ContainSingle().Which.Level.Should().Be(LogEventLevel.Information);
    }
}
