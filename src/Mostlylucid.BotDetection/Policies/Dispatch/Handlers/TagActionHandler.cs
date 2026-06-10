using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.Tag"/> by attaching a labelled marker
///     to <see cref="HttpContext.Items"/>. Downstream code that wants to
///     surface the tag (logs, dashboard, response-shaping middleware) reads
///     the canonical key <c>stylobot.policy.tag.{tag.Name}</c>.
///
///     <para>
///         Tags never block, throttle, or short-circuit -- they are pure
///         metadata. The handler returns
///         <see cref="PolicyDispatchResult.FallThrough"/> so the legacy
///         enforcement path runs unchanged.
///     </para>
/// </summary>
public sealed class TagActionHandler : IPolicyActionHandler
{
    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.Tag);

    /// <inheritdoc />
    public Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        if (action is not PolicyAction.Tag tag) return Task.FromResult(PolicyDispatchResult.FallThrough);
        if (string.IsNullOrWhiteSpace(tag.Name)) return Task.FromResult(PolicyDispatchResult.FallThrough);

        var key = PolicyActionDispatcher.TagItemKeyPrefix + tag.Name;
        context.Items[key] = true;
        return Task.FromResult(PolicyDispatchResult.FallThrough);
    }
}
