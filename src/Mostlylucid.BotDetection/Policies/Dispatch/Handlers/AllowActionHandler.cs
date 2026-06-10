using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.Allow"/> by stamping a marker on
///     <see cref="HttpContext.Items"/> and returning
///     <see cref="PolicyDispatchResult.FallThrough"/>. The legacy
///     <c>DetectionPolicyAction</c> enforcement path reads the marker and
///     skips its block/throttle/challenge branches: the policy stack said
///     "let this through", and the legacy heuristic path must respect that.
///
///     <para>
///         Allow does NOT skip the rest of the legacy middleware (logging,
///         response-completion bookkeeping, etc.) -- it only suppresses
///         enforcement. The request continues to <c>_next</c> as if no
///         block-worthy verdict had landed.
///     </para>
/// </summary>
public sealed class AllowActionHandler : IPolicyActionHandler
{
    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.Allow);

    /// <inheritdoc />
    public Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        // Value carries the rule id so a downstream log line / dashboard tile
        // can attribute the allow decision back to the authored rule.
        context.Items[PolicyActionDispatcher.AllowMarkerItemKey] = rule.Id;
        return Task.FromResult(PolicyDispatchResult.FallThrough);
    }
}
