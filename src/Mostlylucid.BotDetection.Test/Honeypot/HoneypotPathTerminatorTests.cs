using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Honeypot;

namespace Mostlylucid.BotDetection.Test.Honeypot;

public class HoneypotPathTerminatorTests
{
    private static (HoneypotPathTerminator terminator, bool[] nextCalled) Build(
        HoneypotDetectionOptions? options = null)
    {
        options ??= new HoneypotDetectionOptions();
        var monitor = new TestOptionsMonitor<HoneypotDetectionOptions>(options);
        var nextCalled = new[] { false };
        RequestDelegate next = _ =>
        {
            nextCalled[0] = true;
            return Task.CompletedTask;
        };
        return (new HoneypotPathTerminator(next, monitor), nextCalled);
    }

    [Fact]
    public async Task Tier1_Tag_Terminates_With_404_And_Empty_Body()
    {
        // Bug B regression: even when the tagger correctly marked a path
        // as Tier 1 honeypot, the pipeline previously continued to YARP
        // and the upstream answered (200 in the prod env-scanner repro,
        // 404 here). Terminator now short-circuits with a bare 404 --
        // no content-type, no body -- so the scanner can't fingerprint.
        var (terminator, nextCalled) = Build();
        var ctx = new DefaultHttpContext();
        ctx.Items[HoneypotPathTagger.ItemKeyTier] = HoneypotTier.Always;

        await terminator.InvokeAsync(ctx);

        Assert.Equal(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
        Assert.Equal(0, ctx.Response.ContentLength);
        Assert.Null(ctx.Response.ContentType);
        Assert.False(nextCalled[0], "Tier 1 must not call _next -- upstream YARP would see the request.");
    }

    [Fact]
    public async Task Tier2_Tag_Falls_Through()
    {
        // Tier 2 paths (/wp-admin, /actuator) are legitimate on some stacks
        // and the operator may have exempt-listed them. The terminator only
        // hard-stops Tier 1; Tier 2 falls through to whatever the operator's
        // policy chain decides.
        var (terminator, nextCalled) = Build();
        var ctx = new DefaultHttpContext();
        ctx.Items[HoneypotPathTagger.ItemKeyTier] = HoneypotTier.Probable;

        await terminator.InvokeAsync(ctx);

        Assert.True(nextCalled[0]);
        Assert.NotEqual(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task No_Tag_Falls_Through()
    {
        // Normal traffic with no honeypot tag must pass through untouched.
        var (terminator, nextCalled) = Build();
        var ctx = new DefaultHttpContext();

        await terminator.InvokeAsync(ctx);

        Assert.True(nextCalled[0]);
        Assert.NotEqual(StatusCodes.Status404NotFound, ctx.Response.StatusCode);
    }

    [Fact]
    public async Task Disabled_Options_Falls_Through_Even_For_Tier1()
    {
        // When honeypot detection is master-switch-off, the terminator must
        // not act even if a stale tag exists in Items.
        var (terminator, nextCalled) = Build(new HoneypotDetectionOptions { Enabled = false });
        var ctx = new DefaultHttpContext();
        ctx.Items[HoneypotPathTagger.ItemKeyTier] = HoneypotTier.Always;

        await terminator.InvokeAsync(ctx);

        Assert.True(nextCalled[0]);
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }
}