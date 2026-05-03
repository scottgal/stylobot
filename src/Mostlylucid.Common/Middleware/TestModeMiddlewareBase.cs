using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.Common.Middleware;

/// <summary>
///     Base class for middleware that supports test mode via request headers
/// </summary>
/// <typeparam name="TResult">The type of result stored in HttpContext.Items</typeparam>
public abstract class TestModeMiddlewareBase<TResult> where TResult : class
{
    protected readonly ILogger Logger;
    protected readonly RequestDelegate Next;

    protected TestModeMiddlewareBase(RequestDelegate next, ILogger logger)
    {
        Next = next;
        Logger = logger;
    }

    /// <summary>
    ///     Key used to store results in HttpContext.Items
    /// </summary>
    protected abstract string ResultKey { get; }

    /// <summary>
    ///     Header name for test mode override
    /// </summary>
    protected abstract string TestModeHeader { get; }

    /// <summary>
    ///     Whether test mode is enabled
    /// </summary>
    protected abstract bool IsTestModeEnabled { get; }

    /// <summary>
    ///     Create a result from test mode header value
    /// </summary>
    protected abstract TResult? CreateTestModeResult(string testValue);

    /// <summary>
    ///     Process the request and return a result
    /// </summary>
    protected abstract Task<TResult?> ProcessRequestAsync(HttpContext context);

    /// <summary>
    ///     Called after processing to add response headers, etc.
    /// </summary>
    protected virtual void OnResultProcessed(HttpContext context, TResult? result)
    {
    }

    public async Task InvokeAsync(HttpContext context)
    {
        TResult? result = null;

        // Check for test mode
        if (IsTestModeEnabled)
        {
            var testValue = context.Request.Headers[TestModeHeader].FirstOrDefault();
            if (!string.IsNullOrEmpty(testValue))
            {
                Logger.LogDebug("Test mode active: {Header}={Value}", TestModeHeader, testValue);
                result = CreateTestModeResult(testValue);
            }
        }

        // Normal processing if not in test mode
        if (result == null) result = await ProcessRequestAsync(context);

        // Store result and notify
        if (result != null)
        {
            context.Items[ResultKey] = result;
            OnResultProcessed(context, result);
        }

        await Next(context);
    }
}

/// <summary>
///     Extension methods for extracting client IP addresses
/// </summary>
public static class HttpContextIpExtensions
{
    /// <summary>
    ///     Standard headers for forwarded IPs (in priority order)
    /// </summary>
    private static readonly string[] ForwardedHeaders =
    {
        "CF-Connecting-IP", // Cloudflare
        "True-Client-IP", // Akamai, Cloudflare Enterprise
        "X-Real-IP", // Nginx
        "X-Forwarded-For", // Standard proxy header
        "X-Client-IP", // Apache
        "X-Cluster-Client-IP" // Rackspace
    };

    /// <summary>
    ///     Get the client IP address, respecting forwarded headers from proxies/CDNs
    /// </summary>
    public static string? GetClientIpAddress(this HttpContext context)
    {
        // Check forwarded headers first
        foreach (var header in ForwardedHeaders)
        {
            var value = context.Request.Headers[header].FirstOrDefault();
            if (!string.IsNullOrEmpty(value))
            {
                // X-Forwarded-For can contain multiple IPs, take the first (original client)
                var ip = value.Split(',', StringSplitOptions.RemoveEmptyEntries)
                    .FirstOrDefault()?.Trim();

                if (!string.IsNullOrEmpty(ip)) return ip;
            }
        }

        // Fall back to connection remote IP
        return context.Connection.RemoteIpAddress?.ToString();
    }
}