using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;
using StyloExtract.Abstractions;

namespace Mostlylucid.BotDetection.StyloExtract.Actions;

/// <summary>
/// Action policy that replaces an HTML response body with the StyloExtract Markdown output.
/// Content-Type is changed to <c>text/markdown; charset=utf-8</c>.
///
/// Non-HTML responses (JSON, images, binary, 304, 3xx, 204) are passed through unchanged.
///
/// Fail-open: any exception during extraction or body replacement is logged at Warning and
/// the original response is returned without modification. There is no FailClosed option.
///
/// Implementation note: the policy installs a <see cref="BodyInterceptStream"/> before
/// returning <see cref="ActionResult.Allowed"/>. The StyloBot middleware then calls
/// <c>next()</c>, which writes the HTML response into the interceptor. When the interceptor
/// is flushed/disposed, the transform delegate fires and writes Markdown to the original body.
/// </summary>
public sealed class ExtractMarkdownActionPolicy : IActionPolicy
{
    private const string Representation = "markdown";
    internal const string InterceptorInstalledItemKey = "StyloExtract.MarkdownInterceptorInstalled";
    private readonly ILayoutExtractor _extractor;
    // Startup snapshot only (FOSS hard rule: no runtime options-reload). Named options have no
    // IOptions<T> equivalent, so IOptionsFactory<T> -- the non-reload-observing factory
    // IOptionsMonitor/IOptionsSnapshot are themselves built on -- resolves the named section
    // ONCE here; the frozen result is never re-read from the factory again.
    private readonly StyloExtractActionOptions _options;
    private readonly ILogger<ExtractMarkdownActionPolicy> _logger;
    private readonly ResponseBodyCapture _capture;
    private readonly CacheControlWriter _cacheWriter;
    private readonly MarkdownResponseCache _markdownCache;
    private readonly IReadOnlySet<string> _allowedQueryKeys;
    private readonly ContentCacheTelemetry _telemetry;

    public ExtractMarkdownActionPolicy(
        ILayoutExtractor extractor,
        IOptionsFactory<StyloExtractActionOptions> optionsFactory,
        ILogger<ExtractMarkdownActionPolicy> logger,
        ResponseBodyCapture capture,
        CacheControlWriter cacheWriter,
        MarkdownResponseCache? markdownCache = null,
        ContentCacheTelemetry? telemetry = null)
    {
        _extractor = extractor;
        _options = optionsFactory.Create(Name);
        _logger = logger;
        _capture = capture;
        _cacheWriter = cacheWriter;
        _markdownCache = markdownCache ?? new MarkdownResponseCache(_options.TransformedContentCache);
        _allowedQueryKeys = _options.TransformedContentCache.AllowedQueryKeys;
        _telemetry = telemetry ?? new ContentCacheTelemetry();
    }

    /// <inheritdoc />
    public string Name => "extract-markdown";

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Custom;

    /// <inheritdoc />
    public PolicyIntent Intent => PolicyIntent.Pass;

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var opts = _options;

        // A request may match both the normal AI policy and the explicit query override.
        // Only one interceptor may own a response body.
        if (context.Items.ContainsKey(InterceptorInstalledItemKey))
            return ActionResult.Allowed("extract-markdown: interceptor already installed");

        var lease = HttpMethods.IsGet(context.Request.Method)
            ? await _markdownCache.AcquireAsync(
                CacheKeyBuilder.Build(context.Request, Representation,
                    opts.VersionSaltForCache(), _allowedQueryKeys),
                cancellationToken).ConfigureAwait(false)
            : MarkdownCacheLease.Bypass;
        if (lease.Cached is { } cached)
        {
            _telemetry.RecordHit(Name);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/markdown; charset=utf-8";
            context.Response.ContentLength = cached.Length;
            _cacheWriter.Apply(context, opts.Cache);
            await context.Response.Body.WriteAsync(cached, cancellationToken).ConfigureAwait(false);
            context.MarkResponseFromStyloBot();
            return ActionResult.Blocked(StatusCodes.Status200OK, "extract-markdown: cache hit");
        }

        var sourceUri = BuildSourceUri(context.Request);
        var extractionOptions = new ExtractionOptions { Profile = opts.Profile };

        // Install the interceptor. The StyloBot middleware will call next() after this
        // method returns; next() writes HTML into the interceptor buffer. When the buffer
        // is flushed, the transform delegate fires.
        _capture.InstallInterceptor(context, async html =>
        {
            try
            {
                // Evaluate cacheability AFTER upstream has written headers.
                if (!CacheabilityEvaluator.IsCacheable(context))
                {
                    _telemetry.RecordBypass(Name, "response not cacheable");
                    _markdownCache.Discard(lease);
                    return null; // Pass through unchanged.
                }

                var result = await _extractor.ExtractAsync(html, sourceUri, extractionOptions, cancellationToken);
                var mdBytes = Encoding.UTF8.GetBytes(result.Markdown);

                // Update response headers before the body is written.
                context.Response.ContentType = "text/markdown; charset=utf-8";
                context.Response.ContentLength = mdBytes.Length;

                _cacheWriter.Apply(context, opts.Cache);
                _markdownCache.Publish(lease, mdBytes);
                _telemetry.RecordMiss(Name);

                return result.Markdown;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "extract-markdown: extraction failed for {Path}; returning original HTML",
                    LogSanitize(context.Request.Path.Value));
                _telemetry.RecordStoreFailure(Name);
                _markdownCache.Discard(lease);
                return null; // Signal pass-through.
            }
        });
        context.Items[InterceptorInstalledItemKey] = true;
        context.Response.OnCompleted(() =>
        {
            _markdownCache.AbandonUnfilled(lease);
            return Task.CompletedTask;
        });

        return ActionResult.Allowed("extract-markdown: interceptor installed");
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

    // Strip CR/LF from request paths before logging so a crafted request path cannot
    // inject log lines (CodeQL cs/log-injection). Underscore replacement preserves
    // visual length so the redacted value is still debuggable.
    private static string LogSanitize(string? value)
        => value is null ? "" : value.Replace('\r', '_').Replace('\n', '_');
}
