using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Actions;

/// <summary>
/// Action policy that adds a <c>Link: &lt;url&gt;; rel="alternate"; type="text/markdown"</c>
/// header to the response without touching the body. The linked URL follows the configured
/// <see cref="StyloExtractActionOptions.SidecarRouteTemplate"/>.
///
/// Fail-open: any error is logged at Warning and the original response proceeds
/// with only the Link header set (which is always added as it requires no extraction).
/// </summary>
public sealed class ExtractSidecarActionPolicy : IActionPolicy
{
    // Startup snapshot only (FOSS hard rule: no runtime options-reload). Named options have no
    // IOptions<T> equivalent, so IOptionsFactory<T> -- the non-reload-observing factory
    // IOptionsMonitor/IOptionsSnapshot are themselves built on -- resolves the named section
    // ONCE here; the frozen result is never re-read from the factory again.
    private readonly StyloExtractActionOptions _options;
    private readonly ILogger<ExtractSidecarActionPolicy> _logger;

    public ExtractSidecarActionPolicy(
        IOptionsFactory<StyloExtractActionOptions> optionsFactory,
        ILogger<ExtractSidecarActionPolicy> logger)
    {
        _options = optionsFactory.Create(Name);
        _logger = logger;
    }

    /// <inheritdoc />
    public string Name => "extract-sidecar";

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
        var opts = _options;

        try
        {
            var sidecarUrl = BuildSidecarUrl(context.Request, opts.SidecarRouteTemplate);
            context.Response.Headers.Append("Link", $"<{sidecarUrl}>; rel=\"alternate\"; type=\"text/markdown\"");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "extract-sidecar: failed to build Link header for {Path}",
                LogSanitize(context.Request.Path.Value));
        }

        return Task.FromResult(ActionResult.Allowed("extract-sidecar: Link header added"));
    }

    /// <summary>
    /// Builds the sidecar URL from the request and the route template.
    /// <c>{path}</c> interpolates the full request path (without leading slash).
    /// <c>{slug}</c> interpolates the last path segment.
    /// </summary>
    public static string BuildSidecarUrl(HttpRequest request, string template)
    {
        var path = request.Path.Value?.TrimStart('/') ?? string.Empty;
        var slug = path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

        return template
            .Replace("{path}", path, StringComparison.Ordinal)
            .Replace("{slug}", slug, StringComparison.Ordinal);
    }

    // Strip CR/LF from request paths before logging so a crafted request path cannot
    // inject log lines (CodeQL cs/log-injection).
    private static string LogSanitize(string? value)
        => value is null ? "" : value.Replace('\r', '_').Replace('\n', '_');
}
