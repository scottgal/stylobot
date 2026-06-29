using FluentAssertions;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.AspNetPack.Logging;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.AspNetPack.Tests.Logging;

public class LogSinkLoggerProviderTests
{
    [Fact]
    public void Provider_routes_warn_and_above_to_registered_sinks()
    {
        // Mirrors Task A5 acceptance check: warn+ records reach the sink, anything
        // below the configured MinLevel is dropped on the floor.
        var sink = new CapturingSink();
        var opts = new LogSinkProviderOptions { MinLevel = LogLevel.Warning };
        using var provider = new LogSinkLoggerProvider(new[] { sink }, opts);

        var logger = provider.CreateLogger("Yarp.ReverseProxy");
        logger.LogInformation("info -- drop");
        logger.LogWarning("warn -- keep");
        logger.LogError("err -- keep");

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().Contain(r => r.Body.Contains("warn"));
        sink.Records.Should().Contain(r => r.Body.Contains("err"));
        sink.Records.Should().NotContain(r => r.Body.Contains("info"));
    }

    [Fact]
    public void Provider_passes_category_name_through_to_records()
    {
        var sink = new CapturingSink();
        using var provider = new LogSinkLoggerProvider(new[] { sink }, new LogSinkProviderOptions());

        provider.CreateLogger("Yarp.ReverseProxy.Forwarder").LogError("upstream 503");
        provider.CreateLogger("Microsoft.Hosting.Lifetime").LogWarning("shutdown");

        sink.Records.Should().HaveCount(2);
        sink.Records.Should().Contain(r => r.Category == "Yarp.ReverseProxy.Forwarder");
        sink.Records.Should().Contain(r => r.Category == "Microsoft.Hosting.Lifetime");
    }

    [Fact]
    public void Provider_includes_exception_type_and_message_in_body_when_present()
    {
        var sink = new CapturingSink();
        using var provider = new LogSinkLoggerProvider(new[] { sink }, new LogSinkProviderOptions());

        var ex = new InvalidOperationException("upstream gone");
        provider.CreateLogger("Test").LogError(ex, "yarp forwarder failed");

        sink.Records.Should().ContainSingle();
        var rec = sink.Records[0];
        rec.Body.Should().Contain("yarp forwarder failed");
        rec.Body.Should().Contain("InvalidOperationException");
        rec.Body.Should().Contain("upstream gone");
    }

    [Fact]
    public void Provider_is_inert_when_no_sinks_registered()
    {
        // FOSS-only deployment: no commercial OtelMesh, so no IGatewayLogIngestSink
        // gets registered. The provider must not throw and must not allocate
        // anywhere observable.
        using var provider = new LogSinkLoggerProvider(Array.Empty<IGatewayLogIngestSink>(), new LogSinkProviderOptions());
        var logger = provider.CreateLogger("Test");

        // Just confirming the call path is safe; nothing to assert positively.
        logger.LogError("nowhere to go");
    }

    [Fact]
    public void Provider_swallows_sink_exceptions_so_one_bad_sink_does_not_block_others()
    {
        // The interface contract bans throws but we double-belt: a broken
        // commercial pack must not crash the gateway's request that triggered
        // the warning, and must not starve the other registered sinks.
        var good = new CapturingSink();
        var bad  = new ThrowingSink();
        using var provider = new LogSinkLoggerProvider(new IGatewayLogIngestSink[] { bad, good }, new LogSinkProviderOptions());

        provider.CreateLogger("Test").LogError("boom");

        good.Records.Should().ContainSingle();
        good.Records[0].Body.Should().Contain("boom");
    }

    private sealed class CapturingSink : IGatewayLogIngestSink
    {
        public List<GatewayLogEntry> Records { get; } = new();
        public void Record(GatewayLogEntry entry) => Records.Add(entry);
    }

    private sealed class ThrowingSink : IGatewayLogIngestSink
    {
        public void Record(GatewayLogEntry entry) => throw new InvalidOperationException("simulated sink failure");
    }
}
