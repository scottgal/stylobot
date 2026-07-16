namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Priority metadata for one fingerprint, consumed by
///     <c>FingerprintEvictionGuardian</c>'s <c>DecisionNecessity</c> ranking.
///     Mirrors <c>CompactionSignatureInfo</c> on the archive side: the score
///     inputs the guardian needs to decide keep-or-evict without loading the
///     full <see cref="Fingerprint"/> record.
///     <list type="bullet">
///       <item><see cref="BotProbability"/> = <c>cached_bot_probability</c>.</item>
///       <item><see cref="RiskBand"/> = DERIVED at read from the probability
///         (<c>BucketRisk</c>) in the priority scan; never a stored band.</item>
///       <item><see cref="LastSeen"/> = <c>last_seen</c> (recency term).</item>
///       <item><see cref="Protected"/> = <c>claim_status = 'verified'</c>. A
///         protected fingerprint is never evicted regardless of its score.</item>
///     </list>
/// </summary>
public sealed record FingerprintPriorityInfo(
    string FingerprintId,
    double BotProbability,
    string? RiskBand,
    DateTime LastSeen,
    bool Protected);
