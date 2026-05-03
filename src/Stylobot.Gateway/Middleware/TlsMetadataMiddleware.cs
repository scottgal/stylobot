using Microsoft.AspNetCore.Connections.Features;
using Microsoft.AspNetCore.Http;
using Stylobot.Gateway.Configuration;

namespace Stylobot.Gateway.Middleware;

/// <summary>
///     Middleware that retrieves TLS handshake metadata captured by Kestrel connection callbacks.
///     Copies TLS protocol version and cipher suite from connection items to HttpContext.Items.
///
///     This provides native TLS fingerprinting without requiring external reverse proxy.
///     Requires Kestrel to be configured with UseHttpsWithAlpnCapture() extension.
/// </summary>
public class TlsMetadataMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger<TlsMetadataMiddleware> _logger;

    public TlsMetadataMiddleware(RequestDelegate next, ILogger<TlsMetadataMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            // Copy TLS metadata from connection context to HttpContext
            // This data was captured during TLS handshake by Kestrel callbacks
            context.CopyTlsMetadataToHttpContext();
        }
        catch (Exception ex)
        {
            // Don't let TLS metadata extraction break the request
            _logger.LogWarning(ex, "Failed to copy TLS metadata to HttpContext");
        }

        await _next(context);
    }
}

/// <summary>
///     Extension methods for registering TLS metadata middleware
/// </summary>
public static class TlsMetadataMiddlewareExtensions
{
    /// <summary>
    ///     Add TLS metadata capture middleware to the pipeline.
    ///     This must be added early in the pipeline, before YARP transforms.
    /// </summary>
    public static IApplicationBuilder UseTlsMetadataCapture(this IApplicationBuilder app)
    {
        return app.UseMiddleware<TlsMetadataMiddleware>();
    }
}
