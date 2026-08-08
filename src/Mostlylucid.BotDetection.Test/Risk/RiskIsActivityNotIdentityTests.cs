using System.Collections.Generic;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Risk;

/// <summary>
///     CHARACTERISATION baseline for the pending Risk-semantics change. These tests assert
///     what the code does <b>TODAY</b>, so that when the activity-risk ruling is implemented
///     the change lands as a visible diff rather than a silent reinterpretation.
///
///     <para>
///         <b>The ruling being built toward</b> (operator, 2026-08-08): <i>"Risk is a
///         BEHAVIOURAL ASSESSMENT - is the ACTIVITY risky"</i>. Three independent axes:
///         Classification (what it is - categorical, WINS the label), Risk (is the activity
///         risky), Deception (behaviour contradicts claim - surfaced, never averaged in).
///         <b>Unusual is not risky.</b> Mastodon federating is low risk because federating is
///         not a risky activity, not because it matches a norm.
///     </para>
///
///     <para>
///         <b>What these tests establish.</b> <c>RiskBand</c> today is a function of bot
///         PROBABILITY (see <c>SignatureRiskVerdictComposer.ComputeNeutralRiskBand</c>), so it
///         cannot discriminate benign automation from hazardous automation - both land
///         VeryHigh. <c>ThreatScore</c> already does discriminate, cleanly. That is the
///         evidence for making the change ADDITIVE (surface activity-risk via the ThreatBand
///         that already exists) rather than re-meaning RiskBand in place across every consumer
///         and every persisted row.
///     </para>
///
///     <para>
///         <b>NOT the operator P0.</b> The live "Very Low Risk Profile: 98% bot probability"
///         defect was a mode-vs-max aggregation mismatch in
///         <c>SignatureAggregateCache.WarmFromDetections</c>, fixed separately and covered by
///         <c>SignatureAggregateBandProbabilityAgreementTests</c>. An earlier diagnosis
///         attributed that P0 to these semantics; it did not originate here, and re-meaning
///         RiskBand would not have fixed it.
///     </para>
/// </summary>
public class RiskIsActivityNotIdentityTests
{
    /// <summary>
    ///     A confirmed bot doing entirely benign things: high bot probability, no threat
    ///     signals, and no non-UA corroboration of the friendly claim (so no friendly-pin).
    /// </summary>
    private static AggregatedEvidence ConfirmedBotBenignActivity()
    {
        var ledger = new DetectionLedger("risk-bot-benign");
        ledger.AddContribution(DetectionContribution.Bot(
            "UserAgent", "UserAgent",
            confidence: 0.98,
            reason: "Catalogue-identified crawler",
            weight: 3.0,
            botType: BotType.SearchEngine.ToString()));

        return ledger.ToAggregatedEvidence(
            aiRan: true,
            premergedSignals: new Dictionary<string, object>
            {
                [SignalKeys.UserAgentBotType] = BotType.SearchEngine.ToString(),
                [SignalKeys.UserAgentBotName] = "TestCrawler",
                // Benign activity: threat score at the floor.
                [SignalKeys.IntentThreatScore] = 0.02,
            });
    }

    /// <summary>
    ///     Claims something benign, behaves hazardously: a fediverse UA probing admin paths.
    ///     Under the ruling this must come out risky BECAUSE THE ACTIVITY IS RISKY.
    /// </summary>
    private static AggregatedEvidence ClaimsBenignBehavesHazardously()
    {
        var ledger = new DetectionLedger("risk-deceptive");
        ledger.AddContribution(DetectionContribution.Bot(
            "UserAgent", "UserAgent",
            confidence: 0.60,
            reason: "Claims fediverse instance",
            weight: 1.0,
            botType: BotType.SocialMediaBot.ToString()));

        return ledger.ToAggregatedEvidence(
            aiRan: true,
            premergedSignals: new Dictionary<string, object>
            {
                [SignalKeys.UserAgentBotType] = BotType.SocialMediaBot.ToString(),
                [SignalKeys.UserAgentBotName] = "Mastodon",
                // Hazardous ACTIVITY - this, not the identity mismatch, must drive risk.
                [SignalKeys.IntentThreatScore] = 0.95,
            });
    }

    /// <summary>
    ///     TODAY: a confirmed bot doing benign things is classified a bot AND banded VeryHigh,
    ///     because the neutral band is driven by bot probability. Correct under the current
    ///     human-to-bot definition of RiskBand; wrong under the activity-risk ruling, where
    ///     benign activity should not be high risk regardless of how certainly it is a bot.
    /// </summary>
    [Fact]
    public void Today_benign_automation_bands_high_because_risk_tracks_probability()
    {
        var evidence = ConfirmedBotBenignActivity();

        // Classification: unambiguously a bot. This axis must WIN the label, and stays
        // correct under the ruling - only the band's meaning is due to change.
        Assert.Equal(BotType.SearchEngine, evidence.PrimaryBotType);
        Assert.True(
            evidence.BotProbability >= 0.9,
            $"precondition: expected a confirmed bot, got {evidence.BotProbability:F3}");

        // The band tracks probability, not activity.
        Assert.Equal(RiskBand.VeryHigh, evidence.RiskBand);
    }

    /// <summary>
    ///     THE POINT. Benign automation and hazardous automation land in the SAME band, so
    ///     RiskBand carries no activity-risk information at all today. Whatever the fix, this
    ///     equality is what has to break.
    /// </summary>
    [Fact]
    public void Today_RiskBand_cannot_tell_benign_automation_from_hazardous_automation()
    {
        var benign = ConfirmedBotBenignActivity();
        var deceptive = ClaimsBenignBehavesHazardously();

        Assert.Equal(benign.RiskBand, deceptive.RiskBand);
        Assert.Equal(RiskBand.VeryHigh, deceptive.RiskBand);
    }

    /// <summary>
    ///     The activity-risk axis ALREADY EXISTS and already works: ThreatScore separates the
    ///     two cases cleanly while RiskBand collapses them. This is the argument for making
    ///     the ruling additive - surface the axis that already discriminates, rather than
    ///     re-meaning a value that ~22 consumers and every persisted row already read.
    /// </summary>
    [Fact]
    public void ThreatScore_already_discriminates_activity_risk_where_RiskBand_does_not()
    {
        var benign = ConfirmedBotBenignActivity();
        var deceptive = ClaimsBenignBehavesHazardously();

        Assert.True(
            deceptive.ThreatScore > benign.ThreatScore,
            $"hazardous activity ({deceptive.ThreatScore:F2}) must score above benign "
            + $"activity ({benign.ThreatScore:F2}) on the activity-risk axis");

        // ...and it is the ONLY axis of the two that does.
        Assert.Equal(benign.RiskBand, deceptive.RiskBand);
    }
}
