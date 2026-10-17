namespace Mostlylucid.BotDetection.UI.Hubs;

/// <summary>
///     SignalR hub contract for real-time bot detection dashboard updates.
///     Uses a beacon-only pattern: the server sends lightweight invalidation signals
///     and clients fetch updated data via HTMX partial endpoints.
///     No data payloads are sent over SignalR except for attack arc animations.
/// </summary>
public interface IStyloBotDashboardHub
{
    /// <summary>
    ///     Lightweight invalidation signal. Tells connected clients that a specific
    ///     data category has changed without sending the full data payload.
    ///     The HTMX coordinator uses this to trigger OOB partial refreshes.
    /// </summary>
    /// <param name="signal">
    ///     The invalidation signal name, matching widget <c>data-sb-depends</c> values
    ///     (e.g., "summary", "signature", "countries", "clusters", "useragents"),
    ///     or a specific signature hash for per-signature updates.
    /// </param>
    Task BroadcastInvalidation(string signal);

    /// <summary>
    ///     Lightweight attack arc signal for the world map visualization.
    ///     Carries only the minimum data needed to render an arc animation.
    ///     This is the ONLY method that carries a data payload.
    /// </summary>
    Task BroadcastAttackArc(string countryCode, string riskBand);

    /// <summary>
    ///     Policy Stack live-update beacon (B6). Tells every browser viewing
    ///     a SbPolicyStack rooted at -- or under -- <paramref name="scopeKey"/>
    ///     that the rule corpus at that scope changed. Clients re-fetch the
    ///     affected row partial via HTMX <c>hx-swap-oob</c>; the beacon itself
    ///     carries only the scope key so no rule data leaves the server over
    ///     SignalR. The corresponding group join/leave methods are
    ///     <c>JoinPolicyGroup</c> / <c>LeavePolicyGroup</c> on the hub.
    /// </summary>
    Task PolicyChanged(string scopeKey);

    /// <summary>
    ///     Fingerprint name-slot dirty beacon (HR2). Fires after an operator
    ///     pencil rename lands -- either on this gateway (the editor POST emits
    ///     locally) or on a peer via the HR1 Redis subscriber. Every dashboard
    ///     browser viewing the row finds the matching <c>.fp-name[data-fp-id]</c>
    ///     wrapper and HTMX-swaps it from
    ///     <c>/api/v1/commercial/fingerprints/{id}/given-name/read</c> so the
    ///     row repaints without the operator pressing F5.
    ///     <para>
    ///         Payload is intentionally minimal: <paramref name="fingerprintId"/>
    ///         identifies the row, <paramref name="slot"/> indicates which name
    ///         slot moved (<c>given</c> / <c>llm</c> / <c>induced</c>). No name
    ///         data crosses SignalR -- the swap fetch is the carrier.
    ///     </para>
    /// </summary>
    Task FingerprintDirty(string fingerprintId, string slot);

    /// <summary>
    ///     Structured dirty beacon emitted once per coalescing flush window
    ///     (Plan 3, Task 2). Carries the monotonic tick at which the flush fired
    ///     and the closed set of surface names that changed during the window.
    ///     <para>
    ///         Clients compare <see cref="DashboardDirtyBeacon.Tick"/> against
    ///         each widget's <c>data-sb-tick</c> attribute to skip widgets that
    ///         are already current, avoiding unnecessary partial fetches.
    ///     </para>
    ///     <para>
    ///         <b>ADDITIVE — existing consumers of
    ///         <see cref="BroadcastInvalidation"/> are unaffected.</b> New
    ///         clients may subscribe to this method instead of (or in addition
    ///         to) <see cref="BroadcastInvalidation"/> for richer tick-versioned
    ///         behaviour. The policy-stack and other legacy consumers continue to
    ///         use <see cref="BroadcastInvalidation"/> unchanged.
    ///     </para>
    /// </summary>
    Task BroadcastDirty(DashboardDirtyBeacon beacon);
}
