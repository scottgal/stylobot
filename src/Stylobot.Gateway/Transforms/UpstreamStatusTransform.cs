using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Stylobot.Gateway.Transforms;

/// <summary>
///     YARP response transform that stamps the real origin's HTTP status code
///     into <see cref="HttpContext.Items"/> before any downstream ASP.NET
///     Core middleware can read (or overwrite) the response status.
///     <para>
///         Stamps <c>StyloBot.ProxyTiming.UpstreamStatusCode</c> as a boxed
///         <c>int</c> -- but ONLY when <see cref="ResponseTransformContext.ProxyResponse"/>
///         is non-null, i.e. the request actually reached the destination via
///         <c>MapReverseProxy</c>. Honeypot / blocked / throttled responses are
///         resolved by StyloBot's own enforcement gates BEFORE the proxy ever
///         runs (see <c>UseBotDetection() -&gt; UseDetectionPolicies() -&gt;
///         UseHoneypotTermination() -&gt; ... -&gt; MapReverseProxy()</c>), so this
///         transform never executes for them -- the key stays absent, which is
///         the correct "no real origin call" signal, not missing data.
///     </para>
///     <para>
///         Consumer: <c>Mostlylucid.BotDetection.Middleware.BotDetectionMiddleware
///         .ResolveUpstreamStatusCode</c>, which duplicates <see cref="StatusCodeKey"/>
///         as a literal because the core detection project cannot reference this
///         Gateway host project. Read side is fed into
///         <c>DashboardDetectionEvent.UpstreamStatusCode</c> for the Endpoints
///         table's UPSTREAM/RETURNED columns.
///     </para>
/// </summary>
public static class UpstreamStatusTransform
{
    public const string StatusCodeKey = "StyloBot.ProxyTiming.UpstreamStatusCode";

    /// <summary>
    ///     Register the response transform with the YARP transform builder context.
    ///     Called alongside <see cref="UpstreamTimingTransform.AddUpstreamTimingTransforms"/>
    ///     from the gateway's <c>AddTransforms</c> block.
    /// </summary>
    public static void AddUpstreamStatusTransform(this TransformBuilderContext context)
    {
        context.ResponseTransforms.Add(new StatusStampTransform());
    }

    /// <summary>Exposed for direct unit testing without spinning up the transform pipeline.</summary>
    internal static ValueTask ApplyResponseStatusStamp(ResponseTransformContext context)
    {
        if (context.ProxyResponse is not null)
            context.HttpContext.Items[StatusCodeKey] = (int)context.ProxyResponse.StatusCode;

        return ValueTask.CompletedTask;
    }

    private sealed class StatusStampTransform : ResponseTransform
    {
        public override ValueTask ApplyAsync(ResponseTransformContext context) => ApplyResponseStatusStamp(context);
    }
}
