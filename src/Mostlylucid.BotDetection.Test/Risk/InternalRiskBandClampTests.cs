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

    /// <summary>
    ///     Integration-shaped test for the bug found post-deploy 2026-06-19:
    ///     <see cref="DetectionLedgerExtensions.ToAggregatedEvidence"/> built the
    ///     verdict BEFORE the local-IP override promoted BotType to Internal, so
    ///     the composer never saw BotType="Internal" and could not fire the
    ///     clamp. This test exercises the full ledger ▸ evidence build with the
    ///     IpIsLocal signal set, and asserts the resulting evidence carries
    ///     RiskBand.Low (the composer ran with BotType=Internal and the friendly-pin
    ///     fired). If a future refactor inverts the order again, this test fails
    ///     before the dashboard does.
    /// </summary>
    /// <summary>
    ///     Second post-deploy bug, 2026-06-19 ~22:38 UTC: after the IpIsLocal
    ///     routing fix moved the local-IP override BEFORE the verdict, prod
    ///     fresh-built detections STILL showed RiskBand=VeryHigh because the
    ///     YAML bot-pattern catalogue maps "StyloBot Internal" -> Tool, and
    ///     DetermineRiskVerdict's `yamlType ?? botType` null-coalesce let
    ///     Tool win over the upstream-supplied Internal. The UA pattern is
    ///     spoofable; the network position is not. This test pins the
    ///     precedence so the catalogue can never override a network-position
    ///     Internal classification.
    /// </summary>
    [Fact]
    public void ToAggregatedEvidence_with_IpIsLocal_AND_StyloBotInternal_UA_still_clamps()
    {
        var ledger = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionLedger("test-internal-yaml-precedence");
        ledger.AddContribution(new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 1.0,
            Weight = 1.0,
            Reason = "StyloBot.Internal UA matched developer-tools catalogue",
        });
        var signals = new System.Collections.Generic.Dictionary<string, object>
        {
            [Mostlylucid.BotDetection.Models.SignalKeys.IpIsLocal] = true,
            [Mostlylucid.BotDetection.Models.SignalKeys.UserAgent] = "StyloBot.Internal/6.0.0",
            [Mostlylucid.BotDetection.Models.SignalKeys.UserAgentBotName] = "StyloBot Internal",
            [Mostlylucid.BotDetection.Models.SignalKeys.UserAgentBotType] = "Tool",
        };

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.Equal(RiskBand.Low, evidence.RiskBand);
    }

    [Fact]
    public void ToAggregatedEvidence_with_IpIsLocal_clamps_RiskBand_to_Low()
    {
        var ledger = new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionLedger("test-internal-routing");
        ledger.AddContribution(new Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger.DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 1.0,
            Weight = 1.0,
            Reason = "StyloBot.Internal UA matched",
        });
        var signals = new System.Collections.Generic.Dictionary<string, object>
        {
            [Mostlylucid.BotDetection.Models.SignalKeys.IpIsLocal] = true,
            [Mostlylucid.BotDetection.Models.SignalKeys.UserAgent] = "StyloBot.Internal/6.0.0",
            [Mostlylucid.BotDetection.Models.SignalKeys.UserAgentFamily] = "StyloBot.Internal",
        };

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.Equal(RiskBand.Low, evidence.RiskBand);
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