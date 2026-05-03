using System.Security.Cryptography;
using System.Text;
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
    private readonly bool _allowInsecureAdminAccess;
    private readonly ILogger<AdminSecretMiddleware> _logger;

    public AdminSecretMiddleware(
        RequestDelegate next,
        IOptions<GatewayOptions> options,
        ILogger<AdminSecretMiddleware> logger)
    {
        _next = next;
        _adminBasePath = options.Value.AdminBasePath;
        _adminSecret = options.Value.AdminSecret;
        _allowInsecureAdminAccess = options.Value.AllowInsecureAdminAccess;
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

        // No secret configured = deny by default (fail closed)
        if (string.IsNullOrEmpty(_adminSecret))
        {
            if (_allowInsecureAdminAccess)
            {
                _logger.LogWarning(
                    "Admin endpoints are running without ADMIN_SECRET because ADMIN_ALLOW_INSECURE=true. " +
                    "Do not use this mode in production.");
                await _next(context);
                return;
            }

            _logger.LogError(
                "Admin endpoint access denied: ADMIN_SECRET is not configured. " +
                "Set ADMIN_SECRET or explicitly set ADMIN_ALLOW_INSECURE=true for non-production environments.");
            context.Response.StatusCode = 503;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsJsonAsync(new
            {
                error = "admin_unavailable",
                message = "Admin API is disabled until ADMIN_SECRET is configured"
            });
            return;
        }

        // Check for secret header (timing-safe comparison to prevent side-channel attacks)
        if (!context.Request.Headers.TryGetValue("X-Admin-Secret", out var providedSecret) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(providedSecret.ToString()),
                Encoding.UTF8.GetBytes(_adminSecret)))
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
