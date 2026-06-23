using Microsoft.AspNetCore.Http;

namespace Mostlylucid.BotDetection.EndpointPolicies;

/// <summary>
///     Lightweight transport classifier used by
///     <see cref="IEndpointPolicyResolver"/> pre-detection. Mirrors the
///     broad strokes of <c>TransportProtocolContributor</c> (Wave 0) but
///     runs synchronously without DI -- the resolver needs a class label
///     before <see cref="Mostlylucid.BotDetection.Middleware.BotDetectionMiddleware"/> runs.
/// </summary>
/// <remarks>
///     Returned values match the <c>EndpointPolicyRule.Transport</c>
///     accepted strings: <c>document / api / grpc / grpc-web / websocket /
///     sse / static</c>. Defaults to <c>document</c> when nothing more
///     specific matches.
/// </remarks>
public static class TransportClassifier
{
    private static readonly string[] StaticExtensions =
    [
        ".css", ".js", ".mjs", ".map",
        ".png", ".jpg", ".jpeg", ".gif", ".svg", ".webp", ".ico", ".bmp",
        ".woff", ".woff2", ".ttf", ".otf", ".eot",
        ".mp4", ".webm", ".mp3", ".ogg", ".wav",
        ".pdf"
    ];

    public static string Classify(HttpRequest request)
    {
        // WebSocket upgrade -- most specific.
        if (IsWebSocketUpgrade(request)) return "websocket";

        // gRPC content-type takes precedence over Accept.
        var contentType = request.ContentType ?? "";
        if (contentType.Contains("application/grpc-web", StringComparison.OrdinalIgnoreCase))
            return "grpc-web";
        if (contentType.Contains("application/grpc", StringComparison.OrdinalIgnoreCase))
            return "grpc";

        var accept = request.Headers["Accept"].FirstOrDefault() ?? "";

        // Server-Sent Events
        if (accept.Contains("text/event-stream", StringComparison.OrdinalIgnoreCase))
            return "sse";

        // Static asset by extension -- handles browsers + crawlers alike.
        if (IsStaticAsset(request.Path.Value)) return "static";

        // API path prefix OR JSON-shaped Accept.
        var path = request.Path.Value ?? "";
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)) return "api";
        if (accept.Contains("application/json", StringComparison.OrdinalIgnoreCase))
            return "api";

        // Default -- a document-shaped GET / form POST / etc.
        return "document";
    }

    private static bool IsWebSocketUpgrade(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Upgrade", out var upgrade)) return false;
        foreach (var value in upgrade)
            if (value is not null &&
                value.Contains("websocket", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static bool IsStaticAsset(string? path)
    {
        if (string.IsNullOrEmpty(path)) return false;
        var dot = path.LastIndexOf('.');
        if (dot < 0 || dot == path.Length - 1) return false;
        var ext = path.AsSpan(dot);
        foreach (var known in StaticExtensions)
            if (ext.Equals(known, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    ///     Resolves the client's HTTP protocol version. Edge-injected
    ///     <c>X-Client-HTTP-Version</c> (or <c>Sb-Http-Version</c> compat)
    ///     wins when behind a reverse proxy that terminates h2/h3.
    /// </summary>
    public static string ClassifyProtocolVersion(HttpRequest request)
    {
        var injected = request.Headers["X-Client-HTTP-Version"].FirstOrDefault();
        if (!string.IsNullOrEmpty(injected)) return Normalise(injected);

        injected = request.Headers["Sb-Http-Version"].FirstOrDefault();
        if (!string.IsNullOrEmpty(injected)) return Normalise(injected);

        // HttpRequest.Protocol returns "HTTP/1.1", "HTTP/2", "HTTP/3".
        return Normalise(request.Protocol);
    }

    private static string Normalise(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";
        var slash = raw.IndexOf('/');
        var version = slash >= 0 ? raw[(slash + 1)..] : raw;
        return version.Trim().ToLowerInvariant();
    }
}
