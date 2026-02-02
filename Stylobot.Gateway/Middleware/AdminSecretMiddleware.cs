using Microsoft.Extensions.Options;
using Stylobot.Gateway.Configuration;

namespace Stylobot.Gateway.Middleware;

/// <summary>
/// Middleware to protect admin endpoints with a secret header.
/// </summary>
public class AdminSecretMiddleware
{
    private readonly RequestDelegate _next;
    private readonly string _adminBasePath;
    private readonly string? _adminSecret;
    private readonly ILogger<AdminSecretMiddleware> _logger;

    public AdminSecretMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        ILogger<AdminSecretMiddleware> logger)
    {
        _next = next;
        _adminBasePath = options.Value.AdminBasePath;
        _adminSecret = options.Value.AdminSecret;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // Only protect admin paths
        if (!context.Request.Path.StartsWithSegments(_adminBasePath))
        {
            await _next(context);
            return;
        }

        // No secret configured = allow all admin access
        if (string.IsNullOrEmpty(_adminSecret))
        {
            await _next(context);
            return;
        }

        // Check for secret header
        if (!context.Request.Headers.TryGetValue("X-Admin-Secret", out var providedSecret) ||
            providedSecret != _adminSecret)
        {
            _logger.LogWarning("Unauthorized admin access attempt from {RemoteIp}",
                context.Connection.RemoteIpAddress);

            context.Response.StatusCode = 401;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "unauthorized",
                message = "X-Admin-Secret header required"
            });
            return;
        }

        await _next(context);
    }
}

/// <summary>
/// Extension methods for admin secret middleware.
/// </summary>
public static class AdminSecretMiddlewareExtensions
{
    /// <summary>
    /// Use admin secret middleware if ADMIN_SECRET is configured.
    /// </summary>
    public static IApplicationBuilder UseAdminSecretMiddleware(this IApplicationBuilder app)
    {
        return app.UseMiddleware<AdminSecretMiddleware>();
    }
}
