using System.Diagnostics;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Stylobot.Gateway.Transforms;

/// <summary>
///     YARP transform pair that records the time spent between sending the
///     proxied request to the upstream and receiving the upstream's response.
///     <para>
///         Stamps two <see cref="HttpContext.Items"/> keys:
///         <list type="bullet">
///             <item>
///                 <c>StyloBot.ProxyTiming.UpstreamStartTicks</c> -- value of
///                 <see cref="Stopwatch.GetTimestamp" /> just before the
///                 forwarded request leaves the gateway.
///             </item>
///             <item>
///                 <c>StyloBot.ProxyTiming.UpstreamElapsedMs</c> -- elapsed
///                 milliseconds between the request leaving and the response
///                 arriving, recorded once the response transform runs.
///             </item>
///         </list>
///     </para>
///     <para>
///         Consumers (today: <c>AspNetPackEnrichmentMiddleware</c> in the
///         commercial pack, post-pipeline) read <c>UpstreamElapsedMs</c> to
///         split observed wall-clock duration into upstream RTT vs the
///         gateway's own StyloBot processing time. The keys are stamped only
///         for requests that actually hit YARP -- in-process endpoints
///         (admin, dashboard, /metrics) leave the keys absent so consumers
///         can render an honest "no upstream" cell.
///     </para>
/// </summary>
public static class UpstreamTimingTransform
{
    public const string StartTicksKey  = "StyloBot.ProxyTiming.UpstreamStartTicks";
    public const string ElapsedMsKey   = "StyloBot.ProxyTiming.UpstreamElapsedMs";

    /// <summary>
    ///     Register the request + response transforms with the YARP transform
    ///     builder context. Called from the gateway's <c>AddTransforms</c> block.
    /// </summary>
    public static void AddUpstreamTimingTransforms(this TransformBuilderContext context)
    {
        context.RequestTransforms.Add(new RequestStampTransform());
        context.ResponseTransforms.Add(new ResponseElapsedTransform());
    }

    private sealed class RequestStampTransform : RequestTransform
    {
        public override ValueTask ApplyAsync(RequestTransformContext context)
        {
            context.HttpContext.Items[StartTicksKey] = Stopwatch.GetTimestamp();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ResponseElapsedTransform : ResponseTransform
    {
        public override ValueTask ApplyAsync(ResponseTransformContext context)
        {
            if (context.HttpContext.Items.TryGetValue(StartTicksKey, out var v) && v is long startTicks)
            {
                var elapsedMs = (Stopwatch.GetTimestamp() - startTicks)
                                * 1000.0 / Stopwatch.Frequency;
                context.HttpContext.Items[ElapsedMsKey] = elapsedMs;
            }
            return ValueTask.CompletedTask;
        }
    }
}