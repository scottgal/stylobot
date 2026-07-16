using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Risk;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Identity;

/// <summary>
///     Regression guard for the cached_risk_band parasite: a verified good bot at
///     probability 1.0 read "VeryHigh" on the dashboard because the band was STORED as
///     <c>BucketRisk(1.0)</c>, bypassing the composer's verified -> Low friendly-pin, and
///     every reader showed the stored band. The band is now DERIVED at read from the single
///     fingerprint entry's raw facts via <see cref="FingerprintRiskProjection"/>. These
///     tests pin: verified -> Low, Internal -> Low, and (parity) an unverified high-prob bot
///     still -> VeryHigh so no detection sensitivity was traded away.
/// </summary>
public class FingerprintRiskProjectionTests
{
    private static Fingerprint MakeFingerprint(
        double botProbability, string claimStatus, string? botType, double confidence = 1.0) => new()
    {
        FingerprintId = "fp-test",
        Centroid = new float[8],
        CentroidMaturity = 1,
        Weights = new float[8],
        MemberCount = 1,
        ObservationCount = 12,
        CorrectionCount = 0,
        FirstSeen = DateTime.UtcNow.AddHours(-1),
        LastSeen = DateTime.UtcNow,
        Quality = 1.0,
        InferredClientType = "bot",
        InferredTypeConfidence = confidence,
        InferredTypeChangedAt = DateTime.UtcNow,
        CachedBotProbability = botProbability,
        CachedBotType = botType,
        CachedScoreUpdatedAt = DateTime.UtcNow,
        ClaimStatus = claimStatus,
    };

    [Fact]
    public void VerifiedGoodBot_AtProbabilityOne_DerivesLow_NotVeryHigh()
    {
        // The exact operator bug: verified good bot, probability 1.0.
        var fp = MakeFingerprint(botProbability: 1.0, claimStatus: "verified", botType: "GoodBot");

        var verdict = FingerprintRiskProjection.Compose(fp);

        Assert.True(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.Low, verdict.RiskBand);
        Assert.Equal(ThreatBand.None, verdict.ThreatBand);
        Assert.Equal(RiskProfileLabel.VerifiedCommunity, verdict.RiskProfile);
    }

    [Fact]
    public void InternalClient_AtProbabilityOne_DerivesLow()
    {
        // BotType.Internal carries network-position verification -> friendly clamp.
        var fp = MakeFingerprint(botProbability: 1.0, claimStatus: "unverified", botType: "Internal");

        var verdict = FingerprintRiskProjection.Compose(fp);

        Assert.True(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.Low, verdict.RiskBand);
    }

    [Fact]
    public void UnverifiedBot_AtHighProbability_StillDerivesVeryHigh()
    {
        // Parity: no friendly latch, no corroboration -> the probability bucket stands.
        // Detection sensitivity is NOT traded away by the friendly-pin.
        var fp = MakeFingerprint(botProbability: 1.0, claimStatus: "unverified", botType: "AiBot");

        var verdict = FingerprintRiskProjection.Compose(fp);

        Assert.False(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.VeryHigh, verdict.RiskBand);
    }

    [Fact]
    public void RawFactOverload_MatchesFingerprintOverload()
    {
        // The gate / eviction-scan path uses the raw-fact overload; it must agree with the
        // full-entry overload so the gate and the dashboard can never disagree.
        var fp = MakeFingerprint(botProbability: 1.0, claimStatus: "verified", botType: "SearchEngine");

        var fromEntry = FingerprintRiskProjection.Compose(fp);
        var fromFacts = FingerprintRiskProjection.Compose(
            fp.CachedBotProbability, fp.InferredTypeConfidence, fp.ClaimStatus, fp.CachedBotType, fp.FingerprintId);

        Assert.Equal(fromEntry.RiskBand, fromFacts.RiskBand);
        Assert.Equal(fromEntry.RiskProfile, fromFacts.RiskProfile);
    }
}
