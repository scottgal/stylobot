namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Runtime state of any active "reaction pack" -- an adaptive response
///     escalation that watches upstream-health degradation signals
///     (see <see cref="Mostlylucid.BotDetection.RateLimit.DegradationAtom"/>)
///     and steps a scope (global or an endpoint prefix) up/down through
///     protection levels, overriding the action policy while a level is hot.
/// </summary>
/// <remarks>
///     <para>
///         This interface is a <b>dormant extension seam</b>. The full
///         reaction-pack engine (a YAML-configurable, multi-action escalation
///         state machine with pre/post hooks, <c>.stylopack</c> distribution
///         and a dashboard tab) was prototyped on PR #16 but deliberately not
///         merged: the framework was over-engineered for current needs, so
///         only the reusable primitives were salvaged into
///         <see cref="Mostlylucid.BotDetection.RateLimit.DegradationAtom"/> +
///         <c>AdaptiveScalingTracker</c> (origin-health-driven rate-limit
///         scaling, the single response lever that earned its keep).
///     </para>
///     <para>
///         Nothing in the core product registers this interface, so
///         <c>GetService&lt;IReactionPackContext&gt;()</c> returns
///         <see langword="null"/> and consumers (the dashboard's per-endpoint
///         pack-coverage panel) treat it as "no packs active". A future or
///         commercial reaction-pack engine implements this to light the panel
///         up -- no reflection, no over-broad surface, just one typed contract.
///     </para>
///     <para>
///         Full design rationale, the salvaged-vs-deferred split, and the
///         revival path live in
///         <c>docs/architecture/reaction-packs.md</c>.
///     </para>
/// </remarks>
public interface IReactionPackContext
{
    /// <summary>
    ///     Returns the action-policy name a hot reaction pack wants to force
    ///     for <paramref name="endpoint"/>, or <see langword="null"/> to leave
    ///     <paramref name="currentPolicy"/> untouched. Highest-priority /
    ///     highest-level matching scope wins.
    /// </summary>
    string? GetOverridePolicy(string endpoint, string? currentPolicy);

    /// <summary>
    ///     Snapshot of every currently-active pack level. Empty when no pack
    ///     is escalated. The dashboard reads this to render per-endpoint pack
    ///     coverage.
    /// </summary>
    IReadOnlyList<ReactionPackState> GetActiveStates();
}

/// <summary>
///     One active reaction-pack level: which pack, the scope it covers
///     (<c>"global"</c> or an endpoint-path prefix), the numeric protection
///     level it currently sits at, and the action policy it forces (if any).
/// </summary>
public sealed record ReactionPackState(
    string PackName,
    string Scope,
    int Level,
    string? PolicyName);
