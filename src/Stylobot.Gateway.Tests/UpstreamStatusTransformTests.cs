using Microsoft.AspNetCore.Http;
using Stylobot.Gateway.Transforms;
using Xunit;
using Yarp.ReverseProxy.Transforms;

namespace Stylobot.Gateway.Tests;

/// <summary>
///     Pins <see cref="UpstreamStatusTransform"/>'s response transform -- the
///     write side of the gateway/core contract mirrored by
///     <c>Mostlylucid.BotDetection.Middleware.BotDetectionMiddleware.UpstreamStatusCodeItemKey</c>.
/// </summary>
public class UpstreamStatusTransformTests
{
    [Fact]
    public async Task Stamps_the_real_origin_status_code_when_a_proxy_response_exists()
    {
        var httpContext = new DefaultHttpContext();
        using var proxyResponse = new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
        var context = new ResponseTransformContext
        {
            HttpContext = httpContext,
            ProxyResponse = proxyResponse,
            HeadersCopied = true,
        };

        await UpstreamStatusTransform.ApplyResponseStatusStamp(context);

        Assert.Equal(404, httpContext.Items[UpstreamStatusTransform.StatusCodeKey]);
    }

    [Fact]
    public async Task Does_not_stamp_when_there_is_no_proxy_response()
    {
        // No real origin call happened (e.g. YARP short-circuited before reaching
        // the destination) -- the key must stay absent, not zero, so downstream
        // readers see "no real origin call" rather than a fabricated status.
        var httpContext = new DefaultHttpContext();
        var context = new ResponseTransformContext
        {
            HttpContext = httpContext,
            ProxyResponse = null,
            HeadersCopied = true,
        };

        await UpstreamStatusTransform.ApplyResponseStatusStamp(context);

        Assert.False(httpContext.Items.ContainsKey(UpstreamStatusTransform.StatusCodeKey));
    }
}
