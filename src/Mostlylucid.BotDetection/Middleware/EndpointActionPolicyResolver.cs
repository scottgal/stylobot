using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Attributes;

namespace Mostlylucid.BotDetection.Middleware;

/// <summary>
///     Resolves the per-endpoint action-policy override from the route
///     metadata. Used by <see cref="BotDetectionMiddleware"/> after the
///     honeypot-tag and trust overrides have run, before falling back to
///     the BotType-mapped default action policy.
///     <para>
///     Detection still runs in full -- this only changes the WHAT-do-we-do
///     side once the verdict is in. CLAUDE.md: detect-then-down, never
///     detect-then-skip.
///     </para>
/// </summary>
public static class EndpointActionPolicyResolver
{
    /// <summary>
    ///     Returns the action-policy name to use for the current request,
    ///     or <c>null</c> when no per-endpoint override applies. Precedence:
    ///     <list type="number">
    ///         <item><see cref="BotActionAttribute.PolicyName"/> (more
    ///         specific intent: this attribute exists only to set the
    ///         action policy).</item>
    ///         <item><see cref="BotPolicyAttribute.ActionPolicy"/> (the
    ///         action override on the detection-policy attribute).</item>
    ///     </list>
    /// </summary>
    public static string? ResolveFromEndpoint(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null) return null;

        var actionAttr = endpoint.Metadata.GetMetadata<BotActionAttribute>();
        if (actionAttr is not null && !string.IsNullOrWhiteSpace(actionAttr.PolicyName))
            return actionAttr.PolicyName;

        var policyAttr = endpoint.Metadata.GetMetadata<BotPolicyAttribute>();
        if (policyAttr is not null && !string.IsNullOrWhiteSpace(policyAttr.ActionPolicy))
            return policyAttr.ActionPolicy;

        return null;
    }
}
