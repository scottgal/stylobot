using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Helpers;
using Mostlylucid.BotDetection.Reputation;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Feeds the upstream response outcome back into <see cref="IWebhookEndpointReputation"/>
///     for requests <see cref="Orchestration.Atoms.WebhookSensor"/> flagged as webhook-shaped
///     (stashed as <c>context.Items["sb.webhook.endpoint"]</c>). The upstream status is not
///     known until the upstream has responded, so recording MUST happen after <c>_next</c>
///     runs -- never before -- or the outcome recorded is always the pre-response default.
/// </summary>
public sealed class WebhookOutcomeRecorderMiddleware
{
    private const string WebhookEndpointItemKey = "sb.webhook.endpoint";

    private readonly RequestDelegate _next;
    private readonly IWebhookEndpointReputation _reputation;

    public WebhookOutcomeRecorderMiddleware(RequestDelegate next, IWebhookEndpointReputation reputation)
    {
        _next = next;
        _reputation = reputation;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        await _next(context); // status only known AFTER the upstream responds

        if (context.Items.TryGetValue(WebhookEndpointItemKey, out var value) && value is string endpoint)
        {
            var ip = ClientIpResolver.Resolve(context);
            _reputation.RecordOutcome(endpoint, ip, context.Response.StatusCode);
        }
    }
}
