using Mostlylucid.BotDetection.Policies.Rules;

namespace Mostlylucid.BotDetection.UI.Policies;

/// <summary>
///     Surface the Policy Stack live-update beacon dispatches against. FOSS
///     ships a SignalR implementation by default; commercial hosts swap in a
///     Redis-fanned version so an edit on one node reaches every other node's
///     connected browsers. The interface stays scope-only -- per-row hot/cold
///     details ride out over the HTMX swap, not the broadcast.
/// </summary>
public interface IPolicyChangeBroadcaster
{
    /// <summary>
    ///     Notifies subscribers that the rule set at <paramref name="changedScope"/>
    ///     has been mutated. Implementations are responsible for delivering the
    ///     beacon to every browser whose render scope sits at <paramref name="changedScope"/>
    ///     or anywhere below it -- the client-side glue joins each enclosing
    ///     ancestor's group at render time, so a single broadcast on the changed
    ///     scope hash reaches every descendant browser without the broadcaster
    ///     having to enumerate them.
    /// </summary>
    Task PublishAsync(PolicyScope changedScope, CancellationToken ct = default);
}
