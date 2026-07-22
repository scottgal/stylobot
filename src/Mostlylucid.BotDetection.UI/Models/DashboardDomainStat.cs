namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Raw per-host domain statistics from the event store, used by the
///     <c>/api/v1/domain-stats</c> endpoint and the commercial Domains breakdown.
///     Exposes RAW counts only — one row per distinct observed domain, including
///     internal self-traffic (flagged, not excluded). The licensed-vs-pool
///     classification lives in the commercial overlay, not here.
/// </summary>
/// <param name="Domain">RAW Host as stored in the detections <c>domain</c> column — not pre-filtered or relabelled.</param>
/// <param name="Requests">Non-internal detection rows for this domain in the window (rows where <c>bot_type != 'Internal'</c>), matching the summary's bot universe so the pool reconciles with the Traffic counter.</param>
/// <param name="Bots">Non-internal rows classified bot (<c>bot_probability &gt;= BotFloor</c> and <c>bot_type != 'Internal'</c>) — the same floor and universe the summary uses.</param>
/// <param name="IsInternal">True when the domain is in-cluster gateway self-traffic (health/loopback): classified over ALL rows, so true iff the domain has zero non-Internal rows (its <see cref="Requests"/> is then 0).</param>
public sealed record DashboardDomainStat(
    string Domain,
    long Requests,
    long Bots,
    bool IsInternal);
