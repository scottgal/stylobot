using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Proxy;

/// <summary>
/// Detects the proxy topology in front of the application from incoming request headers.
/// Detection runs on the first observed request and is cached for the process lifetime.
/// Config override: BotDetection:ProxyEnvironment:Mode = Cloudflare | CloudFront | Fastly | Nginx | Generic | Direct.
/// </summary>
public sealed class ProxyEnvironmentDetector : IProxyEnvironment
{
    // -1 = not yet detected; >= 0 = (int)ProxyTopology value
    private int _topologyValue = -1;
    private readonly ILogger<ProxyEnvironmentDetector> _logger;
    private readonly ProxyEnvironmentOptions _options;

    public ProxyEnvironmentDetector(
        ILogger<ProxyEnvironmentDetector> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _options = options.Value.ProxyEnvironment;

        // Pre-cache when mode is explicitly configured
        if (!_options.IsAutoMode && _options.ParsedMode.HasValue)
        {
            _topologyValue = (int)_options.ParsedMode.Value;
            _logger.LogInformation("Proxy topology configured: {Topology}", _options.ParsedMode.Value);
        }
    }

    /// <inheritdoc/>
    public ProxyTopology DetectedTopology =>
        _topologyValue >= 0 ? (ProxyTopology)_topologyValue : ProxyTopology.Direct;

    /// <inheritdoc/>
    public string GetRealClientIp(HttpContext httpContext)
    {
        EnsureDetected(httpContext);

        var headers = httpContext.Request.Headers;
        string? ip = DetectedTopology switch
        {
            ProxyTopology.Cloudflare =>
                headers.TryGetValue("CF-Connecting-IP", out var cfIp) ? cfIp.ToString() : null,

            ProxyTopology.CloudFront =>
                // CloudFront-Viewer-Address format: "1.2.3.4:PORT"
                headers.TryGetValue("CloudFront-Viewer-Address", out var cfAddr)
                    ? cfAddr.ToString().Split(':')[0]
                    : null,

            ProxyTopology.Fastly =>
                headers.TryGetValue("Fastly-Client-IP", out var fastlyIp) ? fastlyIp.ToString() : null,

            ProxyTopology.Nginx =>
                headers.TryGetValue("X-Real-IP", out var realIp) ? realIp.ToString() : null,

            _ => null // Direct + Generic: fall through to XFF / RemoteIpAddress below
        };

        // Fallback: leftmost entry in X-Forwarded-For
        if (string.IsNullOrEmpty(ip) && headers.TryGetValue("X-Forwarded-For", out var xff))
        {
            var first = xff.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
            if (!string.IsNullOrEmpty(first))
                ip = first;
        }

        // Final fallback: raw connection address
        return string.IsNullOrEmpty(ip)
            ? httpContext.Connection.RemoteIpAddress?.ToString() ?? string.Empty
            : ip;
    }

    /// <inheritdoc/>
    public string GetRealScheme(HttpContext httpContext)
    {
        EnsureDetected(httpContext);

        // Cloudflare always terminates TLS - the origin connection is always plain HTTP
        if (DetectedTopology == ProxyTopology.Cloudflare)
            return "https";

        // Standard forwarded-proto header
        if (httpContext.Request.Headers.TryGetValue("X-Forwarded-Proto", out var proto) &&
            !string.IsNullOrEmpty(proto))
            return proto.ToString().Split(',')[0].Trim().ToLowerInvariant();

        return httpContext.Request.Scheme;
    }

    // Triggers one-time topology inference from request headers.
    private void EnsureDetected(HttpContext httpContext)
    {
        if (_topologyValue >= 0)
            return;

        var detected = InferTopology(httpContext.Request.Headers);

        // Thread-safe one-time write: only the first caller wins
        if (Interlocked.CompareExchange(ref _topologyValue, (int)detected, -1) == -1)
            _logger.LogInformation("Proxy topology auto-detected: {Topology}", detected);
    }

    private static ProxyTopology InferTopology(IHeaderDictionary headers)
    {
        if (headers.ContainsKey("CF-Connecting-IP") || headers.ContainsKey("CF-Ray"))
            return ProxyTopology.Cloudflare;

        if (headers.ContainsKey("CloudFront-Viewer-Address") || headers.ContainsKey("X-Amz-Cf-Id"))
            return ProxyTopology.CloudFront;

        if (headers.ContainsKey("Fastly-Client-IP"))
            return ProxyTopology.Fastly;

        if (headers.ContainsKey("X-Real-IP"))
            return ProxyTopology.Nginx;

        if (headers.ContainsKey("X-Forwarded-For"))
            return ProxyTopology.Generic;

        return ProxyTopology.Direct;
    }
}
