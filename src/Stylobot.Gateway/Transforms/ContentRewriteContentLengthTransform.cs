using Mostlylucid.BotDetection.StyloExtract.Internals;
using Yarp.ReverseProxy.Transforms;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Stylobot.Gateway.Transforms;

/// <summary>
///     YARP response transform that clears the upstream <c>Content-Length</c> header whenever a
///     content-cache action policy (<c>content-cache-search</c> / <c>extract-markdown-cache-ai</c>)
///     has installed a <see cref="BodyInterceptStream"/> on <c>HttpContext.Response.Body</c>.
/// </summary>
/// <remarks>
///     <para>
///         P0 root cause (2026-08-17): the interceptor buffers the whole upstream body in memory
///         and, on flush, writes either the original bytes unchanged or a transformed (usually
///         differently-sized) replacement. Neither path reconciled that write against the
///         <c>Content-Length</c> YARP had already copied verbatim from the upstream response --
///         so any rewrite (or even a byte-exact-but-re-encoded pass-through) could promise one
///         byte count and deliver another, and Kestrel throws
///         <c>InvalidOperationException: Response Content-Length mismatch</c> once the real
///         write disagrees with the promise.
///     </para>
///     <para>
///         This transform runs BEFORE the response body is streamed (YARP's default header-copy
///         step has already run by the time custom <see cref="ResponseTransform"/>s execute, and
///         body streaming has not started), so removing the header here is always safe --
///         <c>HttpContext.Response.HasStarted</c> is guaranteed false. With no Content-Length
///         promised, Kestrel falls back to chunked transfer and the interceptor's actual write
///         (see <see cref="BodyInterceptStream.FlushAsync"/>, which now sets the real byte count
///         explicitly before writing) is authoritative instead of racing a stale promise.
///     </para>
/// </remarks>
public static class ContentRewriteContentLengthTransform
{
    /// <summary>
    ///     Register the response transform with the YARP transform builder context. Called
    ///     alongside <see cref="UpstreamStatusTransform.AddUpstreamStatusTransform"/> and
    ///     <see cref="UpstreamTimingTransform.AddUpstreamTimingTransforms"/> from the gateway's
    ///     <c>AddTransforms</c> block.
    /// </summary>
    public static void AddContentRewriteContentLengthTransform(this TransformBuilderContext context)
    {
        context.ResponseTransforms.Add(new ContentLengthClearTransform());
    }

    /// <summary>Exposed for direct unit testing without spinning up the transform pipeline.</summary>
    internal static ValueTask ApplyContentLengthClear(ResponseTransformContext context)
    {
        if (context.HttpContext.Response.Body is BodyInterceptStream)
            context.HttpContext.Response.Headers.Remove("Content-Length");

        return ValueTask.CompletedTask;
    }

    private sealed class ContentLengthClearTransform : ResponseTransform
    {
        public override ValueTask ApplyAsync(ResponseTransformContext context) => ApplyContentLengthClear(context);
    }
}
