using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Services;

/// <summary>
///     Pin the verdict-gate composition rule between the per-signature aggregate
///     (sliding window scope = this exact IP+UA) and the per-fingerprint cached
///     verdict (metastable identity scope = this visitor across IP+UA rotation).
///
///     Rule: take whichever source has the more recent timestamp. The fingerprint
///     source survives rotation, so when it's fresher the visitor inherits their
///     prior verdict instead of paying for a fresh pipeline pass.
/// </summary>
public class SignatureVerdictGateIdentityCompositionTests
{
    private const string Sig = "test-sig";
    private const string FpId = "fp-test-1";

    [Fact]
    public void Compose_BothNull_ReturnsNull()
    {
        var result = SignatureVerdictGate.ComposeVerdicts(Sig, sig: null, id: null);
        Assert.Null(result);
    }

    [Fact]
    public void Compose_OnlySignature_ReturnsSignatureUnchanged()
    {
        var sig = MakeSignatureVerdict(prob: 0.3, ageSeconds: 30);
        var result = SignatureVerdictGate.ComposeVerdicts(Sig, sig, id: null);

        Assert.Same(sig, result);
        Assert.Null(result!.IdentityFingerprintId);
        Assert.False(result.FromIdentityCache);
    }

    [Fact]
    public void Compose_OnlyIdentity_SynthesisesFromFingerprint()
    {
        var id = MakeIdentityVerdict(prob: 0.85, obsCount: 25, ageSeconds: 30);
        var result = SignatureVerdictGate.ComposeVerdicts(Sig, sig: null, id);

        Assert.NotNull(result);
        Assert.Equal(0.85, result!.BotProbability);
        Assert.Equal(FpId, result.IdentityFingerprintId);
        Assert.True(result.FromIdentityCache);
        // Band derived from prob (no stored band): BucketRisk(0.85, conf=1.0) scaled=0.85 >= 0.80 -> VeryHigh.
        Assert.Equal(RiskBand.VeryHigh, result.RiskBand);
        // Confidence ramp: full at 10+ observations.
        Assert.Equal(1.0, result.Confidence);
    }

    [Fact]
    public void Compose_FresherIdentity_WinsOverStaleSignature()
    {
        var sig = MakeSignatureVerdict(prob: 0.2, ageSeconds: 600); // 10 min old, looks human
        var id = MakeIdentityVerdict(prob: 0.9, obsCount: 50, ageSeconds: 5);

        var result = SignatureVerdictGate.ComposeVerdicts(Sig, sig, id);

        Assert.NotNull(result);
        Assert.True(result!.FromIdentityCache,
            "fresher fingerprint cache must win over stale signature aggregate");
        Assert.Equal(0.9, result.BotProbability);
        Assert.Equal(FpId, result.IdentityFingerprintId);
    }

    [Fact]
    public void Compose_FresherSignature_WinsOverStaleIdentity_AndCarriesFingerprintId()
    {
        var sig = MakeSignatureVerdict(prob: 0.4, ageSeconds: 5);
        var id = MakeIdentityVerdict(prob: 0.95, obsCount: 50, ageSeconds: 600);

        var result = SignatureVerdictGate.ComposeVerdicts(Sig, sig, id);

        Assert.NotNull(result);
        Assert.False(result!.FromIdentityCache,
            "fresher signature aggregate must win over stale fingerprint cache");
        Assert.Equal(0.4, result.BotProbability);
        // Even when signature wins, the fingerprint id rides along so the dashboard sees a
        // continuous identity across the gate's decisions.
        Assert.Equal(FpId, result.IdentityFingerprintId);
    }

    [Fact]
    public void Compose_FewObservations_HasReducedConfidence()
    {
        // Identity verdict synthesised from a fingerprint with only 3 observations.
        // Confidence ramp matches SignatureCoordinator's: linear up to 10, then full.
        var id = MakeIdentityVerdict(prob: 0.7, obsCount: 3, ageSeconds: 5);
        var result = SignatureVerdictGate.ComposeVerdicts(Sig, sig: null, id);

        Assert.NotNull(result);
        Assert.Equal(0.3, result!.Confidence, precision: 3);
    }

    [Fact]
    public void Compose_DerivesBandFromProbability()
    {
        // No band is stored on the identity verdict; SynthesiseFromIdentity DERIVES it from
        // the probability (verified-aware) via FingerprintRiskProjection -- there is no
        // "unparseable stored band" path any more, the band is always computed.
        var id = MakeIdentityVerdict(prob: 0.5, obsCount: 10, ageSeconds: 5);
        var result = SignatureVerdictGate.ComposeVerdicts(Sig, sig: null, id);

        Assert.NotNull(result);
        // prob=0.5 * (0.5 + 0.5*1.0) = 0.5 scaled. Per BucketRisk boundaries (0.20/0.25/0.45/0.60/0.80):
        // 0.5 falls in [0.45, 0.60] -> Medium.
        Assert.Equal(RiskBand.Medium, result!.RiskBand);
    }

    [Fact]
    public void Compose_VerifiedIdentity_AtHighProbability_DerivesLow()
    {
        // The class fix reaches the live gate too: a verified good bot at probability 1.0
        // synthesises RiskBand.Low (friendly-pin), never BucketRisk(1.0)=VeryHigh.
        var id = MakeIdentityVerdict(
            prob: 1.0, obsCount: 20, ageSeconds: 5, claimStatus: "verified", botType: "GoodBot");
        var result = SignatureVerdictGate.ComposeVerdicts(Sig, sig: null, id);

        Assert.NotNull(result);
        Assert.Equal(RiskBand.Low, result!.RiskBand);
    }

    private static SignatureVerdict MakeSignatureVerdict(double prob, double ageSeconds) => new()
    {
        SignatureId = Sig,
        BotProbability = prob,
        Confidence = 1.0,
        RiskBand = RiskBand.Medium,
        ThreatScore = 0,
        RequestCount = 20,
        LastSeenUtc = DateTime.UtcNow.AddSeconds(-ageSeconds)
    };

    private static IdentityCachedVerdict MakeIdentityVerdict(
        double prob, int obsCount, double ageSeconds,
        string claimStatus = "unverified", string? botType = null) => new(
            FingerprintId: FpId,
            BotProbability: prob,
            ClaimStatus: claimStatus,
            BotType: botType,
            UpdatedAtUtc: DateTime.UtcNow.AddSeconds(-ageSeconds),
            ObservationCount: obsCount,
            InferredClientType: "test-client");
}
