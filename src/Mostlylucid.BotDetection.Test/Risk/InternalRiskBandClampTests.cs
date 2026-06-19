using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Risk;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Risk;

/// <summary>
///     Pins the network-trusted clamp added to
///     <see cref="SignatureRiskVerdictComposer"/>: a request classified as
///     <see cref="BotType.Internal"/> must land on RiskBand.Low regardless of the
///     bot probability. Internal is set only when NetworkHelper.IsLocalIp returned
///     true upstream, so the trust is by network position, not UA identity --
///     the dashboard showing "Internal · Allow · 100% · Risk Profile VeryHigh"
///     was the contradiction this clamp eliminates.
///
///     The clamp is the v0 of the future archetype-alignment evaluator. When the
///     archetype-driven design ships, this rule becomes "Internal AND archetype-
///     aligned"; until then, Internal alone is enough.
/// </summary>
public class InternalRiskBandClampTests
{
    private static SignatureRiskInputs InternalInputs(double botProbability) => new()
    {
        PrimarySignature = "test-internal-sig",
        BotProbability = botProbability,
        Confidence = 1.0,
        RawThreatScore = 0.0,
        FriendlyVerified = false,
        ConfirmedBad = false,
        DeclaredBot = false,
        BotType = nameof(BotType.Internal),
        IsFriendlyBotType = false,
    };

    [Fact]
    public void Internal_with_100pct_botprob_clamps_to_RiskBand_Low()
    {
        var verdict = SignatureRiskVerdictComposer.Compose(InternalInputs(botProbability: 1.0));

        Assert.True(verdict.FriendlyPinFired, "Internal must fire the friendly-pin clamp");
        Assert.Equal(RiskBand.Low, verdict.RiskBand);
    }

    [Fact]
    public void Internal_with_50pct_botprob_still_clamps_to_RiskBand_Low()
    {
        var verdict = SignatureRiskVerdictComposer.Compose(InternalInputs(botProbability: 0.5));

        Assert.True(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.Low, verdict.RiskBand);
    }

    [Fact]
    public void Internal_fires_clamp_reason_for_traceability()
    {
        var verdict = SignatureRiskVerdictComposer.Compose(InternalInputs(botProbability: 1.0));

        Assert.Contains(verdict.Reasons,
            r => r.Contains("Internal", System.StringComparison.OrdinalIgnoreCase)
                 && r.Contains("network-trusted", System.StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void NonInternal_unverified_high_probability_does_NOT_clamp()
    {
        // Sanity: a regular high-probability bot (NOT Internal) must continue to
        // bucket through the neutral path -- the clamp is targeted at Internal,
        // not a blanket "any bot probability stays Low".
        var inputs = InternalInputs(botProbability: 0.95) with { BotType = nameof(BotType.Scraper) };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.False(verdict.FriendlyPinFired,
            "Non-Internal bots must not fire the network-trusted clamp");
        Assert.NotEqual(RiskBand.Low, verdict.RiskBand);
    }

    [Fact]
    public void Internal_with_ConfirmedBad_still_fires_hostile_pin_over_clamp()
    {
        // Behaviour wins over identity for negative signals -- a honeypot hit
        // on an Internal-classified request must still flag hostile. The clamp
        // only runs in the friendly-pin section (which only runs if hostile-pin
        // missed). This pins that ordering so a future refactor can't reverse
        // the precedence and let a compromised internal client off the hook.
        var inputs = InternalInputs(botProbability: 1.0) with { ConfirmedBad = true };

        var verdict = SignatureRiskVerdictComposer.Compose(inputs);

        Assert.True(verdict.HostilePinFired);
        Assert.False(verdict.FriendlyPinFired);
        Assert.Equal(RiskBand.VeryHigh, verdict.RiskBand);
    }
}