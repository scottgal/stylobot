using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch.Handlers;

/// <summary>
///     Handles <see cref="PolicyAction.Observe"/>. In the normal selection
///     loop the dispatcher catches Observe-mode rules BEFORE handler dispatch
///     and short-circuits to its own log+continue branch, so this handler is
///     only reached when a Live-mode rule explicitly authored an
///     <see cref="PolicyAction.Observe"/> action. The semantics are the same:
///     fall through without shaping the response.
/// </summary>
public sealed class ObserveActionHandler : IPolicyActionHandler
{
    /// <inheritdoc />
    public Type HandledAction => typeof(PolicyAction.Observe);

    /// <inheritdoc />
    public Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct)
    {
        // No mark, no header, no response mutation. The dispatcher's
        // structured-log line + decision-log row already record the matched
        // rule; this handler exists for defensive completeness.
        return Task.FromResult(PolicyDispatchResult.FallThrough);
    }
}
