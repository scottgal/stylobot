using Mostlylucid.BotDetection.Auth;

namespace Mostlylucid.BotDetection.Orchestration.Sessions;

/// <summary>
///     Per-fingerprint accumulated state held in the shared per-domain
///     session store. One entry per fingerprint currently active in a
///     session window on a given site. Refreshed on every
///     <see cref="SessionSample"/> raise for the same fingerprint;
///     shaped-eviction is priority-based (see
///     <see cref="RetentionPriority"/>) rather than FIFO or LFU.
/// </summary>
/// <remarks>
///     <para>
///         The session atom reads this aggregate to decide:
///         <list type="bullet">
///             <item>Did the aggregate cross a threshold that would rewrite the fingerprint's cached bot probability / inferred client type? → emit persistence signal.</item>
///             <item>Is the identity still uncertain? → keep in the store longer (high <see cref="RetentionPriority"/>).</item>
///         </list>
///     </para>
///     <para>
///         Kept small and value-typed so aggregation is copy-on-write
///         inside the session store. No mutation on the store side --
///         update = replace the entry with a new record.
///     </para>
/// </remarks>
public sealed record SessionAggregate
{
    public required string FingerprintId { get; init; }
    public required string SiteId { get; init; }
    public required DateTimeOffset FirstSample { get; init; }
    public required DateTimeOffset LastSample { get; init; }
    public required int SampleCount { get; init; }

    /// <summary>
    ///     Rolling mean of per-sample <see cref="SessionSample.BotProbability"/>.
    ///     Used as the aggregate verdict; if the mean crosses the persisted
    ///     fingerprint's cached band, the session atom emits a persistence
    ///     signal.
    /// </summary>
    public required double MeanBotProbability { get; init; }

    /// <summary>
    ///     Peak per-sample bot probability seen in this session window.
    ///     Load-bearing for "one hard hit is enough" cases (honeypot, high
    ///     confidence bot).
    /// </summary>
    public required double MaxBotProbability { get; init; }

    /// <summary>Confidence of the most recent sample. Feeds retention scoring.</summary>
    public required double LatestConfidence { get; init; }

    /// <summary>
    ///     Count of honeypot hits in this session window. Non-zero is
    ///     always shift-worthy -- honeypots are stylobot's own traps and
    ///     survive both closed-loop and outage suppression.
    /// </summary>
    public required int HoneypotHits { get; init; }

    /// <summary>
    ///     Count of upstream 4xx / 5xx responses, keyed by status code.
    ///     Segmented on <see cref="SessionSample.FromUpstream"/> at aggregate
    ///     time so stylobot's own enforcement codes never inflate the
    ///     status-shaped shift signal (closed-loop feedback guard).
    /// </summary>
    public required IReadOnlyDictionary<int, int> UpstreamStatusCounts { get; init; }

    /// <summary>
    ///     Dominant inferred client type across the session window. When
    ///     this shifts (browser → scraper, for instance) the session atom
    ///     treats the aggregate as fingerprint-shifting even if the
    ///     probability delta is small.
    /// </summary>
    public string? DominantClientType { get; init; }

    /// <summary>
    ///     Cached Web Bot Auth (RFC 9421) verification result for this session
    ///     window. Set by <c>WebBotAuthApprovalAtom</c> on the first request
    ///     that presents a <c>Signature</c> + <c>Signature-Input</c> header
    ///     pair; reused on subsequent requests in the same window to avoid
    ///     per-request crypto verification. <c>null</c> when no WBA headers
    ///     have been seen for this fingerprint.
    ///
    ///     Security: contains ONLY public metadata (keyid, outcome, agent name,
    ///     algorithm). Raw signature bytes and signature base strings are never
    ///     stored here.
    /// </summary>
    public WebBotAuthCachedVerdict? WebBotAuthVerdict { get; init; }

    /// <summary>
    ///     Behavioural retention priority for shaped eviction. Higher =
    ///     keep longer.
    ///
    ///     Priority function (see
    ///     <c>reference_session_layer_and_fingerprint_levels</c>):
    ///     <list type="bullet">
    ///         <item>Low confidence + bot probability near 0.5 (uncertain, could go either way) → high priority. These are the identities we are still learning about.</item>
    ///         <item>Any honeypot hit → high priority (always meaningful).</item>
    ///         <item>High confidence + probability far from 0.5 (well-classified) → low priority. We already know what this is.</item>
    ///     </list>
    ///     Read by <c>SessionStore</c>'s retention scorer at cleanup time;
    ///     stored on the aggregate so the scorer is O(1) rather than
    ///     recomputing across the aggregate on every sweep.
    /// </summary>
    public required double RetentionPriority { get; init; }
}