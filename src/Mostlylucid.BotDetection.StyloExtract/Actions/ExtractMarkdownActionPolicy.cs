using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
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
    private readonly ILayoutExtractor _extractor;
    private readonly IOptionsMonitor<StyloExtractActionOptions> _optionsMonitor;
    private readonly ILogger<ExtractMarkdownActionPolicy> _logger;
    private readonly ResponseBodyCapture _capture;
    private readonly CacheControlWriter _cacheWriter;

    public ExtractMarkdownActionPolicy(
        ILayoutExtractor extractor,
        IOptionsMonitor<StyloExtractActionOptions> optionsMonitor,
        ILogger<ExtractMarkdownActionPolicy> logger,
        ResponseBodyCapture capture,
        CacheControlWriter cacheWriter)
    {
        _extractor = extractor;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
        _capture = capture;
        _cacheWriter = cacheWriter;
    }

    /// <inheritdoc />
    public string Name => "extract-markdown";

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Custom;

    /// <inheritdoc />
    public PolicyIntent Intent => PolicyIntent.Pass;

    /// <inheritdoc />
    public Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var opts = _optionsMonitor.Get(Name);

        // EnableQueryOverride / QueryParamName / QueryParamValue describe a debug-time
        // "?format=markdown" override. By the time this policy is dispatched the rule
        // already matched, so checking the query param here gates nothing. The override
        // needs a separate always-on middleware that runs the transform on requests the
        // rule matcher would not have dispatched; that is not wired in this pack yet.
        // The option fields remain in StyloExtractActionOptions for the eventual feature
        // so operators do not need a config migration when it ships.

        var sourceUri = BuildSourceUri(context.Request);
        var extractionOptions = new ExtractionOptions { Profile = opts.Profile };

        // Install the interceptor. The StyloBot middleware will call next() after this
        // method returns; next() writes HTML into the interceptor buffer. When the buffer
        // is flushed, the transform delegate fires.
        _capture.InstallInterceptor(context, async html =>
        {
            try
            {
                var result = await _extractor.ExtractAsync(html, sourceUri, extractionOptions, cancellationToken);
                var mdBytes = Encoding.UTF8.GetBytes(result.Markdown);

                // Update response headers before the body is written.
                context.Response.ContentType = "text/markdown; charset=utf-8";
                context.Response.ContentLength = mdBytes.Length;

                _cacheWriter.Apply(context, opts.Cache);

                return result.Markdown;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "extract-markdown: extraction failed for {Path}; returning original HTML",
                    LogSanitize(context.Request.Path.Value));
                return null; // Signal pass-through.
            }
        });

        return Task.FromResult(ActionResult.Allowed("extract-markdown: interceptor installed"));
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
