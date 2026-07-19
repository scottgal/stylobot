using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
/// Establishes the validated rich API-key context before bot detection runs. This deliberately
/// does not challenge or reject requests; API endpoint authorization remains policy-owned.
/// </summary>
public sealed class ApiKeyContextMiddleware
{
    public const string ContextItemKey = "BotDetection.ApiKeyContext";

    private readonly RequestDelegate _next;
    private readonly IApiKeyStore _apiKeyStore;
    private readonly string _headerName;

    public ApiKeyContextMiddleware(
        RequestDelegate next,
        IApiKeyStore apiKeyStore,
        IOptions<BotDetectionOptions> options)
    {
        _next = next;
        _apiKeyStore = apiKeyStore;
        _headerName = options.Value.ApiBypassHeaderName;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        // An absent, blank, invalid, or path-denied key retains the existing public-request
        // behaviour. The API authorization handler owns challenges for protected API endpoints.
        if (!string.IsNullOrWhiteSpace(_headerName) &&
            context.Request.Headers.TryGetValue(_headerName, out var headerValue))
        {
            var presentedKey = headerValue.ToString();
            if (!string.IsNullOrWhiteSpace(presentedKey))
            {
                var (result, _) = _apiKeyStore.ValidateKeyWithReason(
                    presentedKey,
                    context.Request.Path.Value ?? "/");

                if (result is not null)
                    context.Items[ContextItemKey] = result.Context;
            }
        }

        await _next(context);
    }
}

public static class ApiKeyContextMiddlewareExtensions
{
    /// <summary>Place directly before <c>UseBotDetection</c>.</summary>
    public static IApplicationBuilder UseApiKeyContext(this IApplicationBuilder app) =>
        app.UseMiddleware<ApiKeyContextMiddleware>();
}
