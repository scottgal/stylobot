using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.RouteSwap"/> by stashing the retarget
///     on <c>HttpContext.Items</c> and returning
///     <see cref="PolicyDispatchResult.FallThrough"/> -- the request continues
///     through the pipeline and reaches the proxy. The consumer is the
///     commercial host's <c>ProxyPolicyRoutingMiddleware</c> (immediately
///     before <c>MapReverseProxy</c>), which forwards the request to the
///     stashed target via YARP's <see cref="Yarp.ReverseProxy.Forwarder.IHttpForwarder"/>
///     so the client-visible URL stays unchanged.
///
///     <para>
///         FOSS hosts have no consumer and are unaffected: the item is written,
///         nothing reads it, and the request proxies to the route's configured
///         cluster normally. Validation happens here at dispatch time -- the
///         target must be an absolute http(s) URL; anything else falls through
///         with a warning (refuse-don't-break, same as
///         <see cref="RedirectActionHandler"/>).
///     </para>
/// </summary>
public sealed class RouteSwapActionHandler : IPolicyActionHandler
{
    /// <summary>
    ///     <c>HttpContext.Items</c> key carrying the route-swap target
    ///     (absolute http(s) URL) for the consuming proxy middleware.
    ///     Keep in sync with the commercial GatewayHost consumer.
    /// </summary>
    public const string RouteSwapTargetItemKey = "StyloBot.ProxyPolicy.RouteSwapTarget";

    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.RouteSwap);

    /// <inheritdoc />
    public Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        var swap = action as PolicyAction.RouteSwap;

        if (swap is null ||
            !Uri.TryCreate(swap.Target, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            var policyLogger = (Microsoft.Extensions.Logging.ILogger<RouteSwapActionHandler>?)context.RequestServices
                ?.GetService(typeof(Microsoft.Extensions.Logging.ILogger<RouteSwapActionHandler>));
            policyLogger?.LogWarning(
                "RouteSwap action refused for rule {RuleId}: target '{Target}' is not an absolute http(s) URL; falling through",
                rule.Id, swap?.Target);
            return Task.FromResult(PolicyDispatchResult.FallThrough);
        }

        context.Items[RouteSwapTargetItemKey] = uri.AbsoluteUri;
        return Task.FromResult(PolicyDispatchResult.FallThrough);
    }
}
