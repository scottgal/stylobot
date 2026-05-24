namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Live snapshot of an action policy as the dashboard would render it:
///     not the static config, but the *effective* state right now
///     (including the adaptive scaling multiplier once that lands in
///     phase 4).
/// </summary>
/// <param name="Name">Registered policy name (e.g. <c>rate-limit-search</c>).</param>
/// <param name="Intent">Coarse intent for grouping/filtering.</param>
/// <param name="EffectiveParams">
///     Per-policy parameters as the operator should see them. Implementations
///     are free to decide what to expose -- for a throttle this is base delay,
///     jitter, current multiplier; for a rate-limit it's the effective RPS,
///     burst size, over-limit action; for block it's the status code.
/// </param>
/// <param name="CurrentTier">
///     Active degradation tier (<c>nominal</c> / <c>degraded</c> / <c>critical</c>)
///     when adaptive scaling is enabled; <c>null</c> otherwise.
/// </param>
/// <param name="TierEnteredAtUtc">
///     When the current tier was entered. Used for the "time-in-tier" badge
///     on the dashboard.
/// </param>
/// <param name="Stats">Recent firing counts for this policy.</param>
public sealed record PolicyState(
    string Name,
    PolicyIntent Intent,
    IReadOnlyDictionary<string, object> EffectiveParams,
    string? CurrentTier,
    DateTime? TierEnteredAtUtc,
    PolicyFiringStats Stats);

/// <summary>
///     Recent firing counts for a single policy, surfaced on the dashboard's
///     policy tab.
/// </summary>
/// <param name="Hits5m">Total times this policy fired in the last 5 minutes.</param>
/// <param name="DistinctSignatures5m">
///     Distinct visitor signatures the policy applied to in the same window.
/// </param>
/// <param name="CurrentMultiplier">
///     Active multiplier from adaptive scaling. <c>null</c> when the policy
///     isn't subject to adaptive scaling; <c>1.0</c> when it is and the
///     origin is healthy.
/// </param>
public sealed record PolicyFiringStats(
    int Hits5m,
    int DistinctSignatures5m,
    double? CurrentMultiplier);

/// <summary>
///     Read model for the dashboard policy tab. Implementations aggregate
///     across the registry, the firing-stats counter, and (later) the
///     adaptive scaling tracker.
/// </summary>
public interface IPolicyStateProvider
{
    /// <summary>
    ///     Snapshot of every registered policy's current state. The dashboard
    ///     policy tab calls this once per page render; SignalR pushes diffs.
    /// </summary>
    IReadOnlyList<PolicyState> GetAll();

    /// <summary>
    ///     Lookup by policy name. Returns <c>null</c> when the name isn't
    ///     registered (e.g. typo in a per-BotType override).
    /// </summary>
    PolicyState? Get(string policyName);
}
