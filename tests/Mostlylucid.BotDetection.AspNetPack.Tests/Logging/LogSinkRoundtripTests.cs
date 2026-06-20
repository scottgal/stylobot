using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.AspNetPack.Configuration;
using Mostlylucid.BotDetection.AspNetPack.Logging;
using Xunit;

namespace Mostlylucid.BotDetection.AspNetPack.Tests.Logging;

public class LogSinkRoundtripTests
{
    [Fact]
    public void Logger_emit_lands_in_provider_channel_with_sb_fp_attribute_when_active()
    {
        var opts = Options.Create(new LogSinkOptions
        {
            AllowedCategories = new[] { "*" },
            MinLevel = LogLevel.Information
        });
        using var provider = new StyloBotGatewayLoggerProvider(opts);
        var logger = provider.CreateLogger("Test");
        using var activity = new System.Diagnostics.Activity("test")
            .AddBaggage("sb-fp", "fp_abc")
            .Start();

        logger.LogInformation("hello");

        var depth = provider.Channel.Reader.CanCount ? provider.Channel.Reader.Count : -1;
        depth.Should().BeGreaterThan(0);
        provider.Channel.Reader.TryRead(out var record).Should().BeTrue();
        record!.FingerprintId.Should().Be("fp_abc");
        record.Body.Should().Be("hello");
    }
}
