using Mostlylucid.BotDetection.Risk;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Risk;

/// <summary>
///     The operator "correct decision" control applies a ground-truth label as a
///     high-confidence PRIOR on <see cref="SignatureRiskInputs.BotProbability"/>, never a
///     decision-path override. These pin the bias-not-bypass contract: the label moves the
///     verdict in normal cases, but the behaviour pins (confirmed-bad, hostile threat) still
///     run on the biased probability, so an operator cannot whitelist a real attacker.
/// </summary>
public sealed class OperatorCorrectionBiasTests
{
    private static readonly DateTime At = new(2026, 7, 13, 0, 0, 0, DateTimeKind.Utc);

    private static SignatureRiskInputs Base(
        double probability = 0.5, double confidence = 0.9, double rawThreat = 0.0)
        => new()
        {
            PrimarySignature = "sig-test",
            BotProbability = probability,
            Confidence = confidence,
            RawThreatScore = rawThreat,
            FriendlyVerified = false,
            ConfirmedBad = false,
            DeclaredBot = false,
        };

    [Fact]
    public void Human_correction_biases_a_bot_looking_probability_down_and_records_the_reason()
    {
        var inputs = Base(probability: 0.90) with
        {
            Correction = new OperatorCorrectionPrior("human", null, At),
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.Contains(verdict.Reasons, r => r.Contains("operator_correction"));
        Assert.False(verdict.HostilePinFired);
        // 0.90 blended toward 0.05 at weight 0.85 -> ~0.18, so the band drops off VeryHigh.
        Assert.NotEqual(RiskBand.VeryHigh, verdict.RiskBand);
    }

    [Fact]
    public void Bot_correction_biases_a_human_looking_probability_up_and_records_the_reason()
    {
        var withCorrection = SignatureRiskVerdictComposer.Compose(
            Base(probability: 0.10) with
            {
                Correction = new OperatorCorrectionPrior("bot", "Scraper", At),
            });
        var withoutCorrection = SignatureRiskVerdictComposer.Compose(Base(probability: 0.10));

        Assert.Contains(withCorrection.Reasons, r => r.Contains("operator_correction"));
        // The bias raises the band relative to the same un-corrected input (0.10 -> ~0.82).
        Assert.True((int)withCorrection.RiskBand > (int)withoutCorrection.RiskBand);
    }

    [Fact]
    public void Human_correction_does_NOT_bypass_confirmed_bad()
    {
        var inputs = Base(probability: 0.90) with
        {
            Correction = new OperatorCorrectionPrior("human", null, At),
            ConfirmedBad = true,
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        // The operator cannot whitelist a confirmed-bad actor: the hostile pin still fires.
        Assert.True(verdict.HostilePinFired);
    }

    [Fact]
    public void Human_correction_does_NOT_bypass_a_hostile_threat_score()
    {
        var inputs = Base(probability: 0.50, rawThreat: 0.70) with
        {
            Correction = new OperatorCorrectionPrior("human", null, At),
        };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        // rawThreat >= the hostile gate: behaviour still wins over the operator's label.
        Assert.True(verdict.HostilePinFired);
    }

    [Fact]
    public void No_correction_leaves_the_verdict_untouched()
    {
        var verdict = SignatureRiskVerdictComposer.Compose(Base(probability: 0.90));

        Assert.DoesNotContain(verdict.Reasons, r => r.Contains("operator_correction"));
    }
}
