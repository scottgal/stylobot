using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Configuration;

namespace Mostlylucid.BotDetection.UI.Middleware;

/// <summary>
///     Operator admin endpoints used by the setup flow to apply config changes without
///     a manual container restart.
///     <para>
///     <c>POST {BasePath}/reload</c> -- triggers <see cref="IConfigurationRoot.Reload"/>;
///     IOptionsMonitor consumers pick up the new values on their next read. Returns 200.
///     </para>
///     <para>
///     <c>POST {BasePath}/restart</c> -- requests graceful shutdown via
///     <see cref="IHostApplicationLifetime.StopApplication"/>; the supervisor
///     (Docker, systemd, launchctl) starts a fresh process. Returns 202.
///     </para>
///     <para>
///     Auth: <c>Authorization: Bearer &lt;token&gt;</c> where the token matches
///     <see cref="AdminOptions.Token"/>. Comparison is constant-time. There is no
///     anonymous fallback -- when the token is unset the endpoints return 401
///     with a body that tells the operator to configure
///     <c>StyloBot:Dashboard:Admin:Token</c>. Requests never reach the reload or
///     restart handlers without a matching bearer.
///     </para>
/// </summary>
public sealed class StyloBotAdminMiddleware
{
    private readonly RequestDelegate _next;
    private readonly StyloBotDashboardOptions _options;
    private readonly IConfiguration _configuration;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<StyloBotAdminMiddleware> _logger;

    public StyloBotAdminMiddleware(
        RequestDelegate next,
        StyloBotDashboardOptions options,
        IConfiguration configuration,
        IHostApplicationLifetime lifetime,
        ILogger<StyloBotAdminMiddleware> logger)
    {
        _next = next;
        _options = options;
        _configuration = configuration;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var basePath = _options.Admin.BasePath.TrimEnd('/');
        var path = context.Request.Path.Value ?? string.Empty;
        var token = _options.Admin.Token;

        // Anything off the admin base path is none of our business.
        if (!path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Disabled by default. Operator must explicitly set Admin:Enabled=true.
        // When disabled, admin paths fall through to the rest of the pipeline (so
        // they 404 like any other unmatched path -- no admin surface advertised).
        if (!_options.Admin.Enabled)
        {
            await _next(context);
            return;
        }

        if (!HttpMethods.IsPost(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            context.Response.Headers["Allow"] = "POST";
            return;
        }

        // No anonymous path through. If the operator hasn't configured a token, the
        // admin endpoints are unreachable -- 401 with a body that points at the
        // exact config key so the operator knows what to set instead of guessing.
        if (string.IsNullOrEmpty(token))
        {
            _logger.LogWarning(
                "Admin request rejected: no AdminToken configured ({Path} from {IP})",
                path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"stylobot-admin\"";
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(
                "{\"status\":\"admin_disabled\"," +
                "\"message\":\"Set StyloBot:Dashboard:Admin:Token to enable admin endpoints.\"}");
            return;
        }

        if (!TryAuthorize(context, token))
        {
            _logger.LogWarning("Admin request rejected: {Path} from {IP}",
                path, context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.Headers["WWW-Authenticate"] = "Bearer realm=\"stylobot-admin\"";
            return;
        }

        var subPath = path[(basePath.Length + 1)..];
        switch (subPath.TrimEnd('/').ToLowerInvariant())
        {
            case "reload":
                await HandleReloadAsync(context);
                return;
            case "restart":
                await HandleRestartAsync(context);
                return;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private async Task HandleReloadAsync(HttpContext context)
    {
        if (_configuration is IConfigurationRoot root)
        {
            root.Reload();
            _logger.LogInformation("Admin reload: configuration reloaded by {IP}",
                context.Connection.RemoteIpAddress);
            context.Response.StatusCode = StatusCodes.Status200OK;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"status\":\"reloaded\"}");
            return;
        }

        _logger.LogWarning("Admin reload requested but IConfiguration is not IConfigurationRoot ({Type})",
            _configuration.GetType().FullName);
        context.Response.StatusCode = StatusCodes.Status501NotImplemented;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"reload_unsupported\"}");
    }

    private async Task HandleRestartAsync(HttpContext context)
    {
        _logger.LogWarning("Admin restart requested by {IP}; signalling graceful shutdown",
            context.Connection.RemoteIpAddress);

        context.Response.StatusCode = StatusCodes.Status202Accepted;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync("{\"status\":\"restarting\"}");
        await context.Response.Body.FlushAsync();

        // Fire-and-forget so the response actually leaves the wire before the host begins
        // tearing the pipeline down. Without the small delay clients see a torn connection
        // instead of the 202.
        _ = Task.Run(async () =>
        {
            await Task.Delay(250);
            _lifetime.StopApplication();
        });
    }

    private static bool TryAuthorize(HttpContext context, string expected)
    {
        if (!context.Request.Headers.TryGetValue("Authorization", out var header))
            return false;
        var raw = header.ToString();
        const string prefix = "Bearer ";
        if (!raw.StartsWith(prefix, StringComparison.Ordinal))
            return false;
        var provided = raw[prefix.Length..].Trim();
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }
}
