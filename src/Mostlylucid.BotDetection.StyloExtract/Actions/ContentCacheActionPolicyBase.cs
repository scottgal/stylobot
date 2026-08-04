using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Actions;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.StyloExtract.ContentCache;
using Mostlylucid.BotDetection.StyloExtract.Internals;
using Mostlylucid.BotDetection.StyloExtract.Options;

namespace Mostlylucid.BotDetection.StyloExtract.Actions;

/// <summary>
///     Result of a variant transform: the body to write (or null for pass-through) and whether the
///     result should be stored in the cache.
/// </summary>
public sealed record ContentTransformResult(string? Body, bool Store);

/// <summary>
///     Shared content-cache flow for the two visible policies
///     (<c>content-cache-search</c> HTML and <c>extract-markdown-cache-ai</c> Markdown):
///     <list type="number">
///         <item>Evaluate request cacheability (non-GET / auth / cookie → bypass, fail-open to origin).</item>
///         <item>Eligibility gate (variants may restrict who may be served — Markdown only to AI bots).</item>
///         <item>Build the cache key and acquire a lease from the bounded LFU store.</item>
///         <item>On a hit: stamp the response as StyloBot-synthesised, write the cached bytes and
///         <c>Blocked(200)</c> so the pipeline short-circuits BEFORE the endpoint (YARP never runs).</item>
///         <item>On a miss: install a <see cref="BodyInterceptStream"/>; the variant transform runs on
///         flush, the response is re-checked for cacheability, then the lease is published (or discarded).</item>
///     </list>
///     Process-local only — never distributed or persistent. Fail-open is structural: every bypass,
///     error and capture failure lets the request continue to origin.
/// </summary>
public abstract class ContentCacheActionPolicyBase : IActionPolicy, IAsyncDisposable
{
    /// <summary>Startup snapshot only (FOSS hard rule: no runtime options-reload).</summary>
    protected readonly StyloExtractActionOptions Options;
    protected readonly ResponseBodyCapture Capture;
    protected readonly CacheControlWriter CacheWriter;
    protected readonly MarkdownResponseCache Cache;
    protected readonly CacheKeyBuilder KeyBuilder;
    protected readonly CacheabilityEvaluator Cacheability;
    protected readonly IContentCacheTelemetry Telemetry;
    protected readonly ILogger Logger;

    private readonly ContentRepresentation _representation;

    protected ContentCacheActionPolicyBase(
        string policyName,
        ContentRepresentation representation,
        IOptionsFactory<StyloExtractActionOptions> optionsFactory,
        ResponseBodyCapture capture,
        CacheControlWriter cacheWriter,
        MarkdownResponseCache cache,
        CacheKeyBuilder keyBuilder,
        CacheabilityEvaluator cacheability,
        IContentCacheTelemetry telemetry,
        ILogger logger)
    {
        Name = policyName;
        _representation = representation;
        // IOptionsFactory<T> -- the non-reload-observing factory -- resolves the named section ONCE;
        // the frozen result is never re-read from the factory again.
        Options = optionsFactory.Create(policyName);
        Capture = capture;
        CacheWriter = cacheWriter;
        Cache = cache;
        KeyBuilder = keyBuilder;
        Cacheability = cacheability;
        Telemetry = telemetry;
        Logger = logger;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public ActionType ActionType => ActionType.Custom;

    /// <inheritdoc />
    public PolicyIntent Intent => PolicyIntent.Pass;

    /// <summary>Content-Type served on a cache hit (HTML vs Markdown).</summary>
    protected abstract string HitContentType { get; }

    /// <summary>
    ///     Representation gate. The base allows all traffic; the Markdown variant overrides this so it
    ///     only ever serves AI-scraper traffic — a browser routed to it falls through to origin.
    /// </summary>
    protected virtual bool IsEligible(AggregatedEvidence evidence) => true;

    /// <summary>Variant transform, run by the interceptor on flush. The base stores when <see cref="ContentTransformResult.Store"/> is set.</summary>
    protected abstract Task<ContentTransformResult> TransformAsync(
        HttpContext context,
        string html,
        CancellationToken cancellationToken);

    /// <inheritdoc />
    public async Task<ActionResult> ExecuteAsync(
        HttpContext context,
        AggregatedEvidence evidence,
        CancellationToken cancellationToken = default)
    {
        var requestDecision = Cacheability.EvaluateRequest(context.Request);
        if (requestDecision.Decision != CacheabilityDecision.Cacheable)
        {
            Telemetry.Bypass(Name);
            return ActionResult.Allowed($"{Name}: {requestDecision.Reason} bypass");
        }

        if (!IsEligible(evidence))
        {
            Telemetry.Bypass(Name);
            return ActionResult.Allowed($"{Name}: not eligible traffic");
        }

        var key = KeyBuilder.Build(
            context.Request, _representation, Name, Options.VersionSaltForCache(),
            Options.TransformedContentCache.AllowedQueryKeys);
        var lease = await Cache.AcquireAsync(key, cancellationToken).ConfigureAwait(false);

        if (lease.Cached is { } cached)
        {
            // Cache-hit responses are synthesised by StyloBot, NOT upstream. Without this stamp the
            // middleware would report response.from_upstream=true (absent defaults true) and feed
            // DegradationAtom a synthetic upstream 200. See StyloBotResponseSignalExtensions.
            context.MarkResponseFromStyloBot();
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = HitContentType;
            context.Response.ContentLength = cached.Length;
            CacheWriter.Apply(context, Options.Cache);
            Telemetry.Hit(Name);
            await context.Response.Body.WriteAsync(cached, cancellationToken).ConfigureAwait(false);
            return ActionResult.Blocked(StatusCodes.Status200OK, $"{Name}: cache hit");
        }

        if (!lease.Fills)
        {
            // Store disabled or the slot is being filled by a concurrent request — never block on it.
            Telemetry.Bypass(Name);
            return ActionResult.Allowed($"{Name}: cache unavailable, continuing to origin");
        }

        Telemetry.Miss(Name);
        Capture.InstallInterceptor(context, async html =>
        {
            // Response cacheability is checked here, AFTER upstream has written headers/status.
            var responseDecision = Cacheability.EvaluateResponse(context.Response);
            if (responseDecision.Decision != CacheabilityDecision.Cacheable)
            {
                Cache.Discard(lease);
                Telemetry.Bypass(Name);
                return null;
            }

            try
            {
                var result = await TransformAsync(context, html, cancellationToken).ConfigureAwait(false);
                if (result.Store && result.Body is not null)
                {
                    // Publish discards internally when the entry exceeds MaxEntryBytes.
                    Cache.Publish(lease, Encoding.UTF8.GetBytes(result.Body));
                    // Apply cache headers so downstream caches / CDNs respect the policy.
                    CacheWriter.Apply(context, Options.Cache);
                }
                else
                {
                    Cache.Discard(lease);
                }
                return result.Body;
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "{Name}: transform failed for {Path}; pass-through",
                    Name, LogSanitize(context.Request.Path.Value));
                Cache.Discard(lease);
                Telemetry.Eviction(Name);
                return null;
            }
        });
        context.Response.OnCompleted(() =>
        {
            // The transform publishes by setting the slot to ready; a slot still filling here means the
            // response completed without a fill (no body / non-HTML pass-through) — release the lease.
            if (lease.Fills && lease.Slot?.IsFilling == true)
            {
                Cache.AbandonUnfilled(lease);
                Telemetry.Eviction(Name);
            }
            return Task.CompletedTask;
        });
        return ActionResult.Allowed($"{Name}: interceptor installed");
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => Cache.DisposeAsync();

    /// <summary>Strips CR/LF from request paths before logging (CodeQL cs/log-injection).</summary>
    protected static string LogSanitize(string? value)
        => value is null ? string.Empty : value.Replace('\r', '_').Replace('\n', '_');
}
