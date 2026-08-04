using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.ContentCache;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;
using StyloExtract.Abstractions;

namespace Mostlylucid.BotDetection.StyloExtract.Actions;

/// <summary>
///     Markdown content-cache action policy (<c>extract-markdown-cache-ai</c>): serves a bounded,
///     process-local cache of HTML→Markdown transformed responses to verified AI-scraper traffic.
///     On a miss it extracts Markdown from the captured HTML, stores the transformed output and
///     serves it as <c>text/markdown</c>; later hits short-circuit before the endpoint.
///     <para>
///         Representation gate: this policy NEVER serves Markdown to browsers. The eligibility check
///         (<see cref="IsEligible"/>) admits only requests the detection layer classified as
///         <see cref="BotType.AiBot"/> — anything else falls through to origin and gets HTML.
///     </para>
/// </summary>
public sealed class ExtractMarkdownCacheAiActionPolicy : ContentCacheActionPolicyBase
{
    private readonly ILayoutExtractor _extractor;

    public ExtractMarkdownCacheAiActionPolicy(
        ILayoutExtractor extractor,
        IOptionsFactory<StyloExtractActionOptions> optionsFactory,
        ILogger<ExtractMarkdownCacheAiActionPolicy> logger,
        ResponseBodyCapture capture,
        CacheControlWriter cacheWriter,
        MarkdownResponseCache cache,
        CacheKeyBuilder keyBuilder,
        CacheabilityEvaluator cacheability,
        IContentCacheTelemetry telemetry)
        : base(
            "extract-markdown-cache-ai",
            ContentRepresentation.Markdown,
            optionsFactory,
            capture,
            cacheWriter,
            cache,
            keyBuilder,
            cacheability,
            telemetry,
            logger)
    {
        _extractor = extractor;
    }

    /// <inheritdoc />
    protected override string HitContentType => "text/markdown; charset=utf-8";

    /// <inheritdoc />
    protected override bool IsEligible(AggregatedEvidence evidence)
        => evidence.PrimaryBotType == BotType.AiBot;

    /// <inheritdoc />
    protected override async Task<ContentTransformResult> TransformAsync(
        HttpContext context,
        string html,
        CancellationToken cancellationToken)
    {
        var sourceUri = BuildSourceUri(context.Request);
        var result = await _extractor.ExtractAsync(
                html, sourceUri, new ExtractionOptions { Profile = Options.Profile }, cancellationToken)
            .ConfigureAwait(false);

        var mdBytes = Encoding.UTF8.GetBytes(result.Markdown);

        // Update response headers before the body is written.
        context.Response.ContentType = HitContentType;
        context.Response.ContentLength = mdBytes.Length;
        CacheWriter.Apply(context, Options.Cache);

        return new ContentTransformResult(result.Markdown, Store: true);
    }

    private static Uri? BuildSourceUri(HttpRequest request)
    {
        try
        {
            return new Uri($"{request.Scheme}://{request.Host}{request.Path}{request.QueryString}");
        }
        catch
        {
            return null;
        }
    }
}
