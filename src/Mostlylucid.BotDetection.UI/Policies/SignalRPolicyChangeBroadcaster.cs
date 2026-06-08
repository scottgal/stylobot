using Microsoft.AspNetCore.SignalR;
using Mostlylucid.BotDetection.Policies.Rules;
using Mostlylucid.BotDetection.UI.Hubs;

namespace Mostlylucid.BotDetection.UI.Policies;

/// <summary>
///     FOSS default <see cref="IPolicyChangeBroadcaster"/>. Publishes a
///     <c>PolicyChanged</c> client method on the SignalR group named
///     <c>policy:{scopeHash}</c> -- the same hash the view component emits on
///     <c>data-policy-stack-scope</c> at render time. Descendant fan-out is
///     handled on the browser side: a section rendered at Endpoint scope joins
///     all four ancestor groups (Endpoint / Subdomain / Domain / Wildcard) on
///     mount, so a single broadcast on the changed scope's hash reaches every
///     browser that needs to refresh.
///
///     <para>
///     The wire payload is intentionally just the canonical
///     <see cref="PolicyScopeKeys.ScopeKey"/> string -- no rule data, no
///     hash, no metadata. The client looks up the section to refresh by
///     its own scope hash; the broadcast tells it "your scope changed, go
///     fetch fresh rows".
///     </para>
///
///     <para>
///     Commercial hosts replace this with a Redis-fanned version so an edit
///     on one node reaches every other node's browsers. The replacement is
///     a straight <see cref="IPolicyChangeBroadcaster"/> swap; nothing
///     elsewhere in the dashboard cares which implementation is active.
///     </para>
/// </summary>
public sealed class SignalRPolicyChangeBroadcaster : IPolicyChangeBroadcaster
{
    private readonly IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> _hub;

    public SignalRPolicyChangeBroadcaster(IHubContext<StyloBotDashboardHub, IStyloBotDashboardHub> hub)
    {
        _hub = hub;
    }

    /// <inheritdoc />
    public Task PublishAsync(PolicyScope changedScope, CancellationToken ct = default)
    {
        var hash = PolicyScopeKeys.ScopeHash(changedScope);
        var key = PolicyScopeKeys.ScopeKey(changedScope);
        // Strongly-typed hub: PolicyChanged is the contract method, the wire
        // call carries the canonical scope key so the browser doesn't have to
        // re-derive it from the hash.
        return _hub.Clients.Group("policy:" + hash).PolicyChanged(key);
    }
}
