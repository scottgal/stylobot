using System;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Risk;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     The ONE compute site for a fingerprint's derived risk verdict.
///     <para>
///     RiskBand / ThreatBand / RiskProfile are NEVER stored on the fingerprint. The
///     old <c>cached_risk_band</c> column was exactly that mistake: a value DERIVED from
///     the probability (via <c>BucketRisk</c>) written to disk as if it were a fact, so a
///     verified good bot -- probability 1.0 -- had <c>BucketRisk(1.0) = VeryHigh</c>
///     persisted and every reader faithfully showed "VeryHigh", bypassing the composer's
///     verified -> Low friendly-pin. Storing a derived value is a second source of truth
///     that silently disagrees with the raw facts it was derived from.
///     </para>
///     <para>
///     The single source of truth is the fingerprint entry's RAW facts (probability,
///     confidence, claim_status, catalogue bot type). The bands are derived from those on
///     every read, through the SAME <see cref="SignatureRiskVerdictComposer"/> the live
///     per-request path uses -- so a verified fingerprint's friendly-pin fires at read and
///     it resolves to <see cref="RiskBand.Low"/> / <see cref="RiskProfileLabel.VerifiedCommunity"/>.
///     </para>
/// </summary>
public static class FingerprintRiskProjection
{
    /// <summary>Compose the derived verdict from a full fingerprint entry.</summary>
    public static SignatureRiskVerdict Compose(Fingerprint fp) =>
        Compose(
            fp.CachedBotProbability,
            fp.InferredTypeConfidence,
            fp.ClaimStatus,
            fp.CachedBotType,
            fp.FingerprintId);

    /// <summary>
    ///     Compose the derived verdict from the raw facts alone. Used by lightweight
    ///     read paths (the identity verdict gate, the eviction priority scan) that carry
    ///     the facts without materialising the whole <see cref="Fingerprint"/>.
    ///     <para>
    ///     <paramref name="claimStatus"/> == "verified" is the corroborated friendly latch
    ///     (identity confirmed by a non-UA channel), mapped to <c>FriendlyVerified</c> so a
    ///     verified bot pins friendly. <c>RawThreatScore</c> / <c>ConfirmedBad</c> are
    ///     per-request behavioural signals, not durable fingerprint facts, so the read-time
    ///     projection leaves them neutral; a hostile fingerprint still buckets high by its
    ///     stored probability.
    ///     </para>
    /// </summary>
    public static SignatureRiskVerdict Compose(
        double botProbability,
        double confidence,
        string? claimStatus,
        string? botType,
        string primarySignature = "")
    {
        var normalizedType = NormaliseBotType(botType);
        var verified = string.Equals(claimStatus, "verified", StringComparison.OrdinalIgnoreCase);
        var isFriendlyType =
            Enum.TryParse<BotType>(normalizedType, ignoreCase: true, out var parsedType)
            && BotTypeClassification.IsFriendly(parsedType);

        return SignatureRiskVerdictComposer.Compose(new SignatureRiskInputs
        {
            PrimarySignature = primarySignature,
            BotProbability = botProbability,
            Confidence = confidence,
            RawThreatScore = 0.0,
            FriendlyVerified = verified,
            ConfirmedBad = false,
            DeclaredBot = false,
            BotType = normalizedType,
            IsFriendlyBotType = isFriendlyType,
        });
    }

    private static string? NormaliseBotType(string? cachedBotType) =>
        string.IsNullOrEmpty(cachedBotType)
        || cachedBotType.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : cachedBotType;
}
