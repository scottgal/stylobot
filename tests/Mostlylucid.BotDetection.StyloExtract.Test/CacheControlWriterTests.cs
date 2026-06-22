using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;
using Xunit;

namespace Mostlylucid.BotDetection.StyloExtract.Tests;

public sealed class CacheControlWriterTests
{
    private readonly CacheControlWriter _writer = new();

    private static DefaultHttpContext MakeContext() => new();

    // ---------------------------------------------------------------------------
    // Respect mode
    // ---------------------------------------------------------------------------

    [Fact]
    public void Respect_mode_does_not_change_cache_control()
    {
        var context = MakeContext();
        context.Response.Headers[HeaderNames.CacheControl] = "max-age=60";

        _writer.Apply(context, new CacheOverrideOptions { Mode = CacheControlMode.Respect });

        context.Response.Headers[HeaderNames.CacheControl].ToString().Should().Be("max-age=60");
    }

    [Fact]
    public void Respect_mode_leaves_empty_cache_control_alone()
    {
        var context = MakeContext();

        _writer.Apply(context, new CacheOverrideOptions { Mode = CacheControlMode.Respect });

        context.Response.Headers.ContainsKey(HeaderNames.CacheControl).Should().BeFalse();
    }

    // ---------------------------------------------------------------------------
    // Override mode
    // ---------------------------------------------------------------------------

    [Fact]
    public void Override_mode_replaces_existing_cache_control()
    {
        var context = MakeContext();
        context.Response.Headers[HeaderNames.CacheControl] = "no-store";

        _writer.Apply(context, new CacheOverrideOptions
        {
            Mode = CacheControlMode.Override,
            MaxAge = 3600,
            Public = true
        });

        var cc = context.Response.Headers[HeaderNames.CacheControl].ToString();
        cc.Should().Contain("max-age=3600");
        cc.Should().Contain("public");
        cc.Should().NotContain("no-store");
    }

    [Fact]
    public void Override_mode_with_no_directives_removes_cache_control()
    {
        var context = MakeContext();
        context.Response.Headers[HeaderNames.CacheControl] = "max-age=60";

        _writer.Apply(context, new CacheOverrideOptions { Mode = CacheControlMode.Override });

        // No directives configured: header should be absent.
        var cc = context.Response.Headers[HeaderNames.CacheControl].ToString();
        cc.Should().BeEmpty();
    }

    [Fact]
    public void Override_mode_emits_must_revalidate()
    {
        var context = MakeContext();

        _writer.Apply(context, new CacheOverrideOptions
        {
            Mode = CacheControlMode.Override,
            MustRevalidate = true
        });

        context.Response.Headers[HeaderNames.CacheControl].ToString().Should().Contain("must-revalidate");
    }

    [Fact]
    public void Override_mode_emits_no_store()
    {
        var context = MakeContext();

        _writer.Apply(context, new CacheOverrideOptions
        {
            Mode = CacheControlMode.Override,
            NoStore = true
        });

        context.Response.Headers[HeaderNames.CacheControl].ToString().Should().Contain("no-store");
    }

    // ---------------------------------------------------------------------------
    // Add mode
    // ---------------------------------------------------------------------------

    [Fact]
    public void Add_mode_adds_max_age_when_missing()
    {
        var context = MakeContext();

        _writer.Apply(context, new CacheOverrideOptions
        {
            Mode = CacheControlMode.Add,
            MaxAge = 86400
        });

        context.Response.Headers[HeaderNames.CacheControl].ToString().Should().Contain("max-age=86400");
    }

    [Fact]
    public void Add_mode_does_not_duplicate_existing_max_age()
    {
        var context = MakeContext();
        context.Response.Headers[HeaderNames.CacheControl] = "max-age=60";

        _writer.Apply(context, new CacheOverrideOptions
        {
            Mode = CacheControlMode.Add,
            MaxAge = 86400
        });

        var cc = context.Response.Headers[HeaderNames.CacheControl].ToString();
        // The original max-age=60 should be preserved (not duplicated).
        cc.Should().Contain("max-age=60");
        cc.Should().NotContain("max-age=86400");
    }

    [Fact]
    public void Add_mode_adds_public_when_missing()
    {
        var context = MakeContext();
        context.Response.Headers[HeaderNames.CacheControl] = "max-age=60";

        _writer.Apply(context, new CacheOverrideOptions
        {
            Mode = CacheControlMode.Add,
            Public = true
        });

        context.Response.Headers[HeaderNames.CacheControl].ToString().Should().Contain("public");
    }
}
