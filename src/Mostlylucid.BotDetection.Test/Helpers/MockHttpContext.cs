using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Mostlylucid.BotDetection.Test.Helpers;

/// <summary>
///     Helper class to create mock HttpContext instances for testing
/// </summary>
public static class MockHttpContext
{
    /// <summary>
    ///     Creates a minimal ServiceProvider so middleware can call GetService without NullReferenceException.
    /// </summary>
    private static IServiceProvider CreateMinimalServiceProvider()
    {
        return new ServiceCollection().BuildServiceProvider();
    }

    /// <summary>
    ///     Creates a minimal HttpContext with the specified User-Agent header
    /// </summary>
    public static HttpContext CreateWithUserAgent(string? userAgent)
    {
        var context = new DefaultHttpContext { RequestServices = CreateMinimalServiceProvider() };

        if (userAgent != null) context.Request.Headers.UserAgent = userAgent;

        return context;
    }

    /// <summary>
    ///     Creates an HttpContext with multiple headers
    /// </summary>
    public static HttpContext CreateWithHeaders(Dictionary<string, string> headers)
    {
        var context = new DefaultHttpContext { RequestServices = CreateMinimalServiceProvider() };

        foreach (var header in headers) context.Request.Headers[header.Key] = header.Value;

        return context;
    }

    /// <summary>
    ///     Creates an HttpContext with a specific IP address
    /// </summary>
    public static HttpContext CreateWithIpAddress(string ipAddress, string? userAgent = null)
    {
        var context = new DefaultHttpContext { RequestServices = CreateMinimalServiceProvider() };

        // Set RemoteIpAddress
        if (IPAddress.TryParse(ipAddress, out var ip)) context.Connection.RemoteIpAddress = ip;

        if (userAgent != null) context.Request.Headers.UserAgent = userAgent;

        return context;
    }

    /// <summary>
    ///     Creates a realistic browser HttpContext
    /// </summary>
    public static HttpContext CreateRealisticBrowser()
    {
        var context = new DefaultHttpContext { RequestServices = CreateMinimalServiceProvider() };

        // Chrome on Windows User-Agent
        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";
        context.Request.Headers.Accept =
            "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8";
        context.Request.Headers.AcceptLanguage = "en-US,en;q=0.9";
        context.Request.Headers.AcceptEncoding = "gzip, deflate, br";
        context.Request.Headers.CacheControl = "max-age=0";
        context.Request.Headers.Connection = "keep-alive";

        return context;
    }

    /// <summary>
    ///     Creates a suspicious bot-like HttpContext
    /// </summary>
    public static HttpContext CreateSuspiciousBot()
    {
        var context = new DefaultHttpContext { RequestServices = CreateMinimalServiceProvider() };

        // Short, simple user agent
        context.Request.Headers.UserAgent = "curl/7.68.0";

        return context;
    }

    /// <summary>
    ///     Creates a known good bot HttpContext (like Googlebot)
    /// </summary>
    public static HttpContext CreateGooglebot()
    {
        var context = new DefaultHttpContext { RequestServices = CreateMinimalServiceProvider() };

        context.Request.Headers.UserAgent =
            "Mozilla/5.0 (compatible; Googlebot/2.1; +http://www.google.com/bot.html)";

        return context;
    }
}