using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Actions;

/// <summary>
/// Serves a bounded, process-local cache of public HTML responses. On a hit it deliberately
/// blocks the policy pipeline so YARP never contacts the upstream; misses capture a successful
/// HTML response and populate the cache for a later request.
/// </summary>
public sealed class ContentCacheActionPolicy : IActionPolicy, IAsyncDisposable
{
    private readonly StyloExtractActionOptions _options;
    private readonly ResponseBodyCapture _capture;
    private readonly CacheControlWriter _cacheWriter;
    private readonly MarkdownResponseCache _cache;
    private readonly ILogger<ContentCacheActionPolicy> _logger;

    public ContentCacheActionPolicy(
        IOptionsFactory<StyloExtractActionOptions> optionsFactory,
        ResponseBodyCapture capture,
        CacheControlWriter cacheWriter,
        ILogger<ContentCacheActionPolicy> logger)
    {
        _options = optionsFactory.Create(Name);
        _capture = capture;
        _cacheWriter = cacheWriter;
        _logger = logger;
        _cache = new MarkdownResponseCache(_options.TransformedContentCache);
    }

    public string Name => "content-cache";
    public ActionType ActionType => ActionType.Custom;
    public PolicyIntent Intent => PolicyIntent.Pass;

    public async Task<ActionResult> ExecuteAsync(HttpContext context, AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
            return ActionResult.Allowed("content-cache: non-GET bypass");

        var lease = await _cache.AcquireAsync(BuildKey(context.Request), cancellationToken).ConfigureAwait(false);
        if (lease.Cached is { } cached)
        {
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = cached.Length;
            _cacheWriter.Apply(context, _options.Cache);
            await context.Response.Body.WriteAsync(cached, cancellationToken).ConfigureAwait(false);
            return ActionResult.Blocked(StatusCodes.Status200OK, "content-cache: cache hit");
        }

        _capture.InstallInterceptor(context, async html =>
        {
            try
            {
                var body = Encoding.UTF8.GetBytes(html);
                _cache.Publish(lease, body);
                return html;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "content-cache: capture failed for {Path}", context.Request.Path);
                _cache.Discard(lease);
                return null;
            }
        });
        context.Response.OnCompleted(() =>
        {
            _cache.AbandonUnfilled(lease);
            return Task.CompletedTask;
        });
        return ActionResult.Allowed("content-cache: interceptor installed");
    }

    private string BuildKey(HttpRequest request)
    {
        var host = request.Host.Host.ToLowerInvariant();
        var path = request.Path.Value ?? "/";
        var query = request.Query.OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value.ToString())}");
        return $"content|{_options.VersionSaltForCache()}|{host}|{path}|{string.Join("&", query)}";
    }

    public ValueTask DisposeAsync() => _cache.DisposeAsync();
}
