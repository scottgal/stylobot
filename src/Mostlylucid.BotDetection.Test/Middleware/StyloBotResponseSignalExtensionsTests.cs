using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;

namespace Mostlylucid.BotDetection.Test.Middleware;

/// <summary>
///     Pins <see cref="StyloBotResponseSignalExtensions"/> -- the helper
///     stylobot's own status-setting middlewares call to mark responses
///     they synthesised, so detector arms downstream don't read stylobot's
///     own enforcement codes (load-shed 503, policy block 403, throttle
///     429, honeypot 404) as additional bot evidence.
/// </summary>
public class StyloBotResponseSignalExtensionsTests
{
    [Fact]
    public void IsResponseFromUpstream_defaults_true_when_key_absent()
    {
        var ctx = new DefaultHttpContext();
        Assert.True(ctx.IsResponseFromUpstream());
    }

    [Fact]
    public void MarkResponseFromStyloBot_sets_key_to_false()
    {
        var ctx = new DefaultHttpContext();
        ctx.MarkResponseFromStyloBot();
        Assert.False(ctx.IsResponseFromUpstream());
        Assert.Equal(false, ctx.Items[BotDetectionMiddleware.ResponseFromUpstreamKey]);
    }

    [Fact]
    public void IsResponseFromUpstream_returns_true_when_key_explicitly_true()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[BotDetectionMiddleware.ResponseFromUpstreamKey] = true;
        Assert.True(ctx.IsResponseFromUpstream());
    }

    [Fact]
    public void IsResponseFromUpstream_returns_true_when_key_holds_non_bool()
    {
        // Defensive: if something else writes the key with the wrong type,
        // fall back to the upstream default so we don't accidentally
        // suppress bot evidence on a real upstream response.
        var ctx = new DefaultHttpContext();
        ctx.Items[BotDetectionMiddleware.ResponseFromUpstreamKey] = "not a bool";
        Assert.True(ctx.IsResponseFromUpstream());
    }

    [Fact]
    public void MarkResponseFromStyloBot_is_idempotent()
    {
        var ctx = new DefaultHttpContext();
        ctx.MarkResponseFromStyloBot();
        ctx.MarkResponseFromStyloBot();
        Assert.False(ctx.IsResponseFromUpstream());
    }
}
