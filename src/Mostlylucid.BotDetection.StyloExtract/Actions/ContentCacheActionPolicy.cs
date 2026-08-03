using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Middleware;
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
    private const string Representation = "html";

    private readonly StyloExtractActionOptions _options;
    private readonly ResponseBodyCapture _capture;
    private readonly CacheControlWriter _cacheWriter;
    private readonly MarkdownResponseCache _cache;
    private readonly ILogger<ContentCacheActionPolicy> _logger;
    private readonly IReadOnlySet<string> _allowedQueryKeys;
    private readonly ContentCacheTelemetry _telemetry;

    public ContentCacheActionPolicy(
        IOptionsFactory<StyloExtractActionOptions> optionsFactory,
        ResponseBodyCapture capture,
        CacheControlWriter cacheWriter,
        ILogger<ContentCacheActionPolicy> logger,
        ContentCacheTelemetry? telemetry = null)
    {
        _options = optionsFactory.Create(Name);
        _capture = capture;
        _cacheWriter = cacheWriter;
        _logger = logger;
        _cache = new MarkdownResponseCache(_options.TransformedContentCache);
        _allowedQueryKeys = _options.TransformedContentCache.AllowedQueryKeys;
        _telemetry = telemetry ?? new ContentCacheTelemetry();
    }

    public string Name => "content-cache";
    public ActionType ActionType => ActionType.Custom;
    public PolicyIntent Intent => PolicyIntent.Pass;

    public async Task<ActionResult> ExecuteAsync(HttpContext context, AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        if (!HttpMethods.IsGet(context.Request.Method))
            return ActionResult.Allowed("content-cache: non-GET bypass");

        var key = CacheKeyBuilder.Build(context.Request, Representation,
            _options.VersionSaltForCache(), _allowedQueryKeys);

        var lease = await _cache.AcquireAsync(key, cancellationToken).ConfigureAwait(false);
        if (lease.Cached is { } cached)
        {
            _telemetry.RecordHit(Name);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "text/html; charset=utf-8";
            context.Response.ContentLength = cached.Length;
            _cacheWriter.Apply(context, _options.Cache);
            await context.Response.Body.WriteAsync(cached, cancellationToken).ConfigureAwait(false);
            // A cache hit is NOT from the upstream — mark it so DegradationAtom
            // doesn't record a synthetic upstream 200.
            context.MarkResponseFromStyloBot();
            return ActionResult.Blocked(StatusCodes.Status200OK, "content-cache: cache hit");
        }

        _capture.InstallInterceptor(context, async html =>
        {
            try
            {
                // Evaluate cacheability AFTER upstream has written headers.
                if (!CacheabilityEvaluator.IsCacheable(context, "text/html; charset=utf-8"))
                {
                    _telemetry.RecordBypass(Name, "response not cacheable");
                    _cache.Discard(lease);
                    return html; // Pass through unchanged.
                }

                _telemetry.RecordMiss(Name);
                var body = Encoding.UTF8.GetBytes(html);
                _cache.Publish(lease, body);
                return html;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "content-cache: capture failed for {Path}", context.Request.Path);
                _telemetry.RecordStoreFailure(Name);
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

    public ValueTask DisposeAsync() => _cache.DisposeAsync();
}
