namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Raw per-host domain statistics from the event store, used by the
///     <c>/api/v1/domain-stats</c> endpoint and the commercial Domains breakdown.
///     Exposes RAW counts only — one row per distinct observed domain, including
///     internal self-traffic (flagged, not excluded). The licensed-vs-pool
///     classification lives in the commercial overlay, not here.
/// </summary>
/// <param name="Domain">RAW Host as stored in the detections <c>domain</c> column — not pre-filtered or relabelled.</param>
/// <param name="Requests">Total detection rows for this domain in the window.</param>
/// <param name="Bots">Rows classified bot (<c>bot_probability &gt;= BotFloor</c>) — the same floor the summary uses, so numbers reconcile with the Traffic counter.</param>
/// <param name="IsInternal">True when the domain is in-cluster gateway self-traffic (health/loopback): every row is <c>bot_type = 'Internal'</c>.</param>
public sealed record DashboardDomainStat(
    string Domain,
    long Requests,
    long Bots,
    bool IsInternal);
