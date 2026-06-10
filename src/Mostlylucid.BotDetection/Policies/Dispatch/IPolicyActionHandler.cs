using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.Policies.Dispatch;

/// <summary>
///     One handler per concrete <see cref="PolicyAction"/> record subtype.
///     Implementations shape the HTTP response (status + headers + body) or
///     mark <see cref="HttpContext.Items"/> for downstream consumers, and
///     report back to <see cref="PolicyActionDispatcher"/> whether the
///     middleware should short-circuit or fall through to the legacy enum
///     path.
///
///     <para>
///         Handlers are registered as singletons -- they MUST be stateless.
///         Per-request state lives on <see cref="HttpContext"/>; shared state
///         (token buckets) lives in a registered registry the handler reads.
///     </para>
/// </summary>
public interface IPolicyActionHandler
{
    /// <summary>
    ///     The concrete <see cref="PolicyAction"/> subtype this handler
    ///     dispatches. Returned to the dispatcher so it can build a
    ///     <c>typeof(Action) -> handler</c> map at construction time.
    /// </summary>
    Type HandledAction { get; }

    /// <summary>
    ///     Apply <paramref name="action"/> for the matched <paramref name="rule"/>
    ///     to <paramref name="context"/>. Mirrors the legacy response shape
    ///     where one exists (Block, Throttle, Challenge) so downstream gateways
    ///     and monitoring see identical envelopes regardless of which path
    ///     produced them.
    /// </summary>
    /// <param name="context">The active HTTP context.</param>
    /// <param name="rule">The matched rule (carries id, scope, mode).</param>
    /// <param name="action">The action to apply. Always assignable to <see cref="HandledAction"/>.</param>
    /// <param name="ct">Cancellation token for the response write.</param>
    Task<PolicyDispatchResult> HandleAsync(
        HttpContext context,
        PolicyRule rule,
        PolicyAction action,
        CancellationToken ct);
}
