using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

public sealed class VaryByBotTypeTests
{
    private readonly CacheControlWriter _writer = new();

    private static DefaultHttpContext MakeContext() => new();

    [Fact]
    public void VaryByBotType_adds_X_StyloBot_BotType_to_Vary()
    {
        var context = MakeContext();

        _writer.Apply(context, new CacheOverrideOptions { VaryByBotType = true });

        context.Response.Headers[HeaderNames.Vary].ToString().Should().Contain("X-StyloBot-BotType");
    }

    [Fact]
    public void VaryByAccept_adds_Accept_to_Vary()
    {
        var context = MakeContext();

        _writer.Apply(context, new CacheOverrideOptions { VaryByAccept = true });

        context.Response.Headers[HeaderNames.Vary].ToString().Should().Contain("Accept");
    }

    [Fact]
    public void VaryByBotType_preserves_existing_Vary_entries()
    {
        var context = MakeContext();
        context.Response.Headers[HeaderNames.Vary] = "Cookie";

        _writer.Apply(context, new CacheOverrideOptions { VaryByBotType = true });

        var vary = context.Response.Headers[HeaderNames.Vary].ToString();
        vary.Should().Contain("Cookie");
        vary.Should().Contain("X-StyloBot-BotType");
    }

    [Fact]
    public void VaryByBotType_does_not_duplicate_when_already_present()
    {
        var context = MakeContext();
        context.Response.Headers[HeaderNames.Vary] = "X-StyloBot-BotType";

        _writer.Apply(context, new CacheOverrideOptions { VaryByBotType = true });

        var vary = context.Response.Headers[HeaderNames.Vary].ToString();
        // Should not be doubled.
        var count = 0;
        var idx = 0;
        while ((idx = vary.IndexOf("X-StyloBot-BotType", idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            idx += "X-StyloBot-BotType".Length;
        }
        count.Should().Be(1);
    }

    [Fact]
    public void Both_vary_flags_are_added_together()
    {
        var context = MakeContext();

        _writer.Apply(context, new CacheOverrideOptions
        {
            VaryByBotType = true,
            VaryByAccept = true
        });

        var vary = context.Response.Headers[HeaderNames.Vary].ToString();
        vary.Should().Contain("X-StyloBot-BotType");
        vary.Should().Contain("Accept");
    }

    [Fact]
    public void Neither_vary_flag_does_not_add_Vary_header()
    {
        var context = MakeContext();

        _writer.Apply(context, new CacheOverrideOptions());

        context.Response.Headers.ContainsKey(HeaderNames.Vary).Should().BeFalse();
    }
}
