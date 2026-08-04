using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.StyloExtract.ContentCache;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Actions;

/// <summary>
///     HTML content-cache action policy (<c>content-cache-search</c>): serves a bounded,
///     process-local cache of public HTML responses to search-engine traffic. On a hit it
///     deliberately blocks the policy pipeline so YARP never contacts the upstream; misses capture
///     a successful HTML response and populate the cache for a later request.
///     <para>
///         HTML is representation-agnostic, so any requester routed here may be served — the
///         eligibility gate (see <see cref="ContentCacheActionPolicyBase.IsEligible"/>) is only used by
///         the Markdown variant.
///     </para>
/// </summary>
public sealed class ContentCacheSearchActionPolicy : ContentCacheActionPolicyBase
{
    public ContentCacheSearchActionPolicy(
        IOptionsFactory<StyloExtractActionOptions> optionsFactory,
        ILogger<ContentCacheSearchActionPolicy> logger,
        ResponseBodyCapture capture,
        CacheControlWriter cacheWriter,
        MarkdownResponseCache cache,
        CacheKeyBuilder keyBuilder,
        CacheabilityEvaluator cacheability,
        IContentCacheTelemetry telemetry)
        : base(
            "content-cache-search",
            ContentRepresentation.Html,
            optionsFactory,
            capture,
            cacheWriter,
            cache,
            keyBuilder,
            cacheability,
            telemetry,
            logger)
    {
    }

    /// <inheritdoc />
    protected override string HitContentType => "text/html; charset=utf-8";

    /// <inheritdoc />
    protected override Task<ContentTransformResult> TransformAsync(
        HttpContext context,
        string html,
        CancellationToken cancellationToken)
        => Task.FromResult(new ContentTransformResult(html, Store: true));
}
