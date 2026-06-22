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
/// Action policy that leaves the original response body unchanged and adds StyloExtract
/// metadata as response headers:
/// <list type="bullet">
///   <item><c>X-StyloExtract-Title</c></item>
///   <item><c>X-StyloExtract-Template-Id</c></item>
///   <item><c>X-StyloExtract-Template-Version</c></item>
///   <item><c>X-StyloExtract-Match-Status</c></item>
///   <item><c>X-StyloExtract-Markdown-Length</c></item>
/// </list>
/// Fail-open: any extraction error is logged at Warning and the response continues
/// without headers rather than failing the request.
///
/// Implementation note: the policy installs a <see cref="BodyInterceptStream"/> to read
/// the HTML response after downstream writes it, then runs extraction and adds headers.
/// The original HTML body is always written back unchanged.
/// </summary>
public sealed class ExtractHeadersActionPolicy : IActionPolicy
{
    private readonly ILayoutExtractor _extractor;
    private readonly IOptionsMonitor<StyloExtractActionOptions> _optionsMonitor;
    private readonly ILogger<ExtractHeadersActionPolicy> _logger;
    private readonly ResponseBodyCapture _capture;
    private readonly CacheControlWriter _cacheWriter;

    public ExtractHeadersActionPolicy(
        ILayoutExtractor extractor,
        IOptionsMonitor<StyloExtractActionOptions> optionsMonitor,
        ILogger<ExtractHeadersActionPolicy> logger,
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
    public string Name => "extract-headers";

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
        var sourceUri = BuildSourceUri(context.Request);
        var extractionOptions = new ExtractionOptions { Profile = opts.Profile };

        _capture.InstallInterceptor(context, async html =>
        {
            // Run extraction.
            ExtractionResult? result = null;
            try
            {
                result = await _extractor.ExtractAsync(html, sourceUri, extractionOptions, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "extract-headers: extraction failed for {Path}; X-StyloExtract-* headers omitted",
                    context.Request.Path);
                // Return null to pass through original HTML unchanged.
                return null;
            }

            // Add metadata headers. Returning null signals pass-through (write original HTML).
            var headers = context.Response.Headers;
            if (result.Title is not null)
                headers.Append("X-StyloExtract-Title", result.Title);
            if (result.Match.TemplateId.HasValue)
                headers.Append("X-StyloExtract-Template-Id", result.Match.TemplateId.Value.ToString());
            headers.Append("X-StyloExtract-Template-Version", result.Match.TemplateVersion.ToString());
            headers.Append("X-StyloExtract-Match-Status", result.Match.Status.ToString());
            headers.Append("X-StyloExtract-Markdown-Length", result.Markdown.Length.ToString());

            _cacheWriter.Apply(context, opts.Cache);

            // Return null: the original HTML body is passed through unchanged.
            return null;
        });

        return Task.FromResult(ActionResult.Allowed("extract-headers: interceptor installed"));
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
