using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.Proxy;

/// <summary>
/// Resolves the real client IP and scheme from the correct headers
/// for the detected proxy topology in front of this application.
/// </summary>
public interface IProxyEnvironment
{
    /// <summary>The proxy topology that has been detected or configured.</summary>
    ProxyTopology DetectedTopology { get; }

    /// <summary>
    /// Returns the real client IP address for this request,
    /// reading the appropriate header for the detected topology.
    /// Also triggers one-time topology auto-detection if not yet done.
    /// </summary>
    string GetRealClientIp(HttpContext httpContext);

    /// <summary>
    /// Returns the real request scheme (http/https) for this request.
    /// Cloudflare always implies HTTPS. Others fall back to X-Forwarded-Proto.
    /// </summary>
    string GetRealScheme(HttpContext httpContext);
}
