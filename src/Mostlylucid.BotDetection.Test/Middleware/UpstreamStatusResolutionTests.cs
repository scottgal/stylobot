using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Middleware;

namespace Mostlylucid.BotDetection.Test.Middleware;

/// <summary>
///     Pins <see cref="BotDetectionMiddleware.ResolveUpstreamStatusCode"/> --
///     the read side of the gateway's <c>UpstreamStatusTransform</c> response
///     transform (Stylobot.Gateway). The two sides share a literal string key
///     (<c>StyloBot.ProxyTiming.UpstreamStatusCode</c>) because this core
///     project cannot reference the Gateway host project; see
///     <see cref="BotDetectionMiddleware.UpstreamStatusCodeItemKey"/>.
/// </summary>
public class UpstreamStatusResolutionTests
{
    [Fact]
    public void ResolveUpstreamStatusCode_reads_gateway_stamped_value()
    {
        var ctx = new DefaultHttpContext();
        ctx.Items[BotDetectionMiddleware.UpstreamStatusCodeItemKey] = 200;

        var resolved = BotDetectionMiddleware.ResolveUpstreamStatusCode(ctx);

        Assert.Equal(200, resolved);
    }

    [Fact]
    public void ResolveUpstreamStatusCode_is_null_when_request_never_reached_the_origin()
    {
        // Honeypot / blocked / throttled responses short-circuit before
        // MapReverseProxy, so YARP's response transform never runs and never
        // stamps the key. Null is the meaningful "no real origin call" signal,
        // not a missing-data gap.
        var ctx = new DefaultHttpContext();

        var resolved = BotDetectionMiddleware.ResolveUpstreamStatusCode(ctx);

        Assert.Null(resolved);
    }
}
