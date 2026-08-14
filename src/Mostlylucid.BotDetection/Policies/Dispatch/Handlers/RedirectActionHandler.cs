using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.Redirect"/> by writing a redirect
///     (default 302) with a <c>Location</c> header and the same
///     <c>X-Stylobot-Policy</c> attribution the other handlers use. Returns
///     <see cref="PolicyDispatchResult.Handled"/> so the middleware
///     short-circuits -- the request never reaches the proxy.
///
///     <para>
///         Validation happens at dispatch time, not authoring time: the target
///         must be an absolute http(s) URL and the status must land in
///         [300, 400). An invalid pair falls through (request continues) with
///         a warning rather than emitting a malformed redirect -- the same
///         refuse-don't-break philosophy as <see cref="SiteConfigOverrideProvider"/>
///         skip rows and the dispatcher's unknown-action fall-through.
///     </para>
/// </summary>
public sealed class RedirectActionHandler : IPolicyActionHandler
{
    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.Redirect);

    /// <inheritdoc />
    public async Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        var redirect = action as PolicyAction.Redirect;
        var status = redirect?.Status ?? StatusCodes.Status302Found;

        if (redirect is null ||
            !Uri.TryCreate(redirect.Target, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            status is < 300 or >= 400)
        {
            var policyLogger = (Microsoft.Extensions.Logging.ILogger<RedirectActionHandler>?)context.RequestServices
                ?.GetService(typeof(Microsoft.Extensions.Logging.ILogger<RedirectActionHandler>));
            policyLogger?.LogWarning(
                "Redirect action refused for rule {RuleId}: target '{Target}' or status {Status} is invalid; falling through",
                rule.Id, redirect?.Target, status);
            return PolicyDispatchResult.FallThrough;
        }

        context.Response.StatusCode = status;
        context.Response.Headers.Location = uri.AbsoluteUri;
        // Closed-loop feedback gate: mark so the visitor's NEXT request
        // doesn't get bot-boosted by stylobot's own redirect response.
        context.MarkResponseFromStyloBot();
        context.Response.Headers[BlockActionHandler.PolicyHeader] = $"rule-redirect ({rule.Id})";

        return PolicyDispatchResult.Handled;
    }
}
