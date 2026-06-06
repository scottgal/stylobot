using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Observability.Enrichment;
using Serilog;
using Serilog.Sinks.InMemory;

namespace Mostlylucid.BotDetection.Observability.Test;

public class StyloBotLogEnricherTests
{
    [Fact]
    public void Enriches_log_lines_with_stylobot_properties_when_verdict_present()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[SignalKeys.PrimarySignature] = "sig-xyz";
        ctx.Items[BotDetectionMiddleware.IsBotKey] = true;
        ctx.Items[BotDetectionMiddleware.BotProbabilityKey] = 0.91;
        ctx.Items[BotDetectionMiddleware.BotNameKey] = "wp-scanner";

        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var sink = new InMemorySink();
        var serilog = new LoggerConfiguration()
            .Enrich.With(new StyloBotLogEnricher(accessor))
            .WriteTo.Sink(sink)
            .CreateLogger();

        serilog.Information("hello");

        var entry = sink.LogEvents.Should().ContainSingle().Subject;
        entry.Properties["StyloBot_Signature"].ToString().Should().Contain("sig-xyz");
        entry.Properties["StyloBot_IsBot"].ToString().Should().Be("True");
        entry.Properties["StyloBot_BotProbability"].ToString().Should().Contain("0.91");
        entry.Properties["StyloBot_BotName"].ToString().Should().Contain("wp-scanner");
    }

    [Fact]
    public void Adds_no_properties_when_no_HttpContext()
    {
        var accessor = new HttpContextAccessor();
        var sink = new InMemorySink();
        var serilog = new LoggerConfiguration()
            .Enrich.With(new StyloBotLogEnricher(accessor))
            .WriteTo.Sink(sink)
            .CreateLogger();

        serilog.Information("background");

        sink.LogEvents.Single().Properties.Keys.Should().NotContain(k => k.StartsWith("StyloBot_"));
    }
}
