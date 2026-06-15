namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Persistent claim-verification trust knobs. Wires together the
///     request-path verifier contributors (<c>VerifiedBotContributor</c>,
///     <c>FediverseDomainContributor</c>, ...) and the
///     <c>fingerprints.claim_status</c> / <c>verified_at</c> columns added in
///     the 2026-06-15 claim-verify-trust gap fix (#4). The gateway reads
///     <see cref="TrustCacheTtl"/> at request entry: if a fingerprint already
///     carries <c>claim_status='verified'</c> and <c>verified_at</c> is within
///     the TTL, the verifier emits <c>verifiedbot.cached</c> and skips the
///     re-verification work (DNS / NodeInfo round-trips).
///     <para>
///     Every knob is configurable per <c>feedback_all_settings_configurable</c>;
///     no magic numbers in the contributors themselves.
///     </para>
///     Binds at <c>BotDetection:Trust</c>.
/// </summary>
public sealed class TrustOptions
{
    /// <summary>
    ///     How long a successful claim verification is trusted before the
    ///     verifier contributors re-run. Mirrors the existing positive cache
    ///     window on <c>FediverseDomainVerifier</c> (24h) so a single restart
    ///     does not break trust continuity across the persistence boundary.
    ///     Increase for low-volatility traffic, decrease for tighter security
    ///     postures where claim drift matters more than DNS load.
    /// </summary>
    public TimeSpan TrustCacheTtl { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     When true, verifier contributors short-circuit on a within-TTL
    ///     <c>claim_status='verified'</c> row and emit <c>verifiedbot.cached</c>
    ///     instead of re-running the rDNS / NodeInfo path. Default true. Flip
    ///     to false to force re-verification on every request (debugging,
    ///     calibration, or pre-trust-launch shadow comparison).
    /// </summary>
    public bool ShortCircuitOnCachedTrust { get; set; } = true;
}