using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Orchestration.Tests.Unit.Orchestration;

/// <summary>
///     Regression tests for the live-prod chain of bugs surfaced by signature
///     <c>9z3avO7sKTd7NAYY896Yog</c> on www.stylobot.net: an env-scanner from
///     an IP outside any vendor bot range tripped the reputation fast-abort
///     latch (<c>ConfirmedBad</c>) but the dashboard still rendered
///     <c>bot_type=GoodBot</c> + <c>ThreatBand=None</c> + policy
///     <c>Silent Throttle</c> because <see cref="DetectionLedgerExtensions.ToAggregatedEvidence"/>
///     wrote PrimaryBotType from the ledger's UA-derived value and ThreatBand
///     from the signals dictionary -- neither read the composer's verdict.
///
///     The composer was already correct (its tests in
///     <see cref="Risk.SignatureRiskVerdictComposerTests"/> assert
///     HostilePin = true / ThreatBand = Critical for the same inputs); the
///     leak was the AggregatedEvidence build path dropping that information.
/// </summary>
public sealed class HostilePinOverrideTests
{
    [Fact]
    public void ConfirmedBad_With_FriendlyUa_PrimaryBotType_BecomesMaliciousBot()
    {
        var ledger = BuildConfirmedBadLedger(
            uaClaimedBotType: BotType.GoodBot,
            uaClaimedBotName: "Amazonbot");

        var evidence = ledger.ToAggregatedEvidence();

        Assert.Equal(BotType.MaliciousBot, evidence.PrimaryBotType);
    }

    [Fact]
    public void ConfirmedBad_With_FriendlyUa_ThreatBand_BecomesCritical()
    {
        var ledger = BuildConfirmedBadLedger(
            uaClaimedBotType: BotType.GoodBot,
            uaClaimedBotName: "Amazonbot");

        var evidence = ledger.ToAggregatedEvidence();

        Assert.Equal(ThreatBand.Critical, evidence.ThreatBand);
    }

    [Fact]
    public void ConfirmedBad_With_FriendlyUa_ThreatScore_LiftedToBandFloor()
    {
        // Without the lift, ThreatBand=Critical pairs with ThreatScore=0.0
        // (reputation latch has no underlying threat score), and
        // DetectionBroadcastMiddleware nulls score==0 -- the dashboard then
        // renders "Critical, --" which reads as "detector is confused" instead
        // of "reputation latch fired". The lift floors the score at the band's
        // lower bound so the two numbers agree.
        var ledger = BuildConfirmedBadLedger(
            uaClaimedBotType: BotType.GoodBot,
            uaClaimedBotName: "Amazonbot");

        var evidence = ledger.ToAggregatedEvidence();

        Assert.Equal(ThreatBand.Critical, evidence.ThreatBand);
        Assert.True(evidence.ThreatScore >= 0.80,
            $"Expected ThreatScore lifted to >= 0.80 (Critical floor); got {evidence.ThreatScore}");
    }

    [Fact]
    public void FriendlyUa_WithoutHostilePin_KeepsItsBotType()
    {
        // Sanity: the override must only fire when HostilePin = true.
        // A clean friendly bot with no reputation latch stays at its
        // catalogue-derived friendly type (SearchEngine for Googlebot --
        // the catalogue is now authoritative over the contribution's hint).
        // ThreatBand is whatever the signals produce; the override does not run.
        var ledger = new DetectionLedger("test-sig");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 1.0,
            Reason = "Verified Googlebot UA",
            BotType = "GoodBot",
            BotName = "Googlebot"
        });

        var evidence = ledger.ToAggregatedEvidence();

        // Catalogue authority promotes "Googlebot" to SearchEngine. Either
        // friendly classification proves the override did NOT demote to
        // a hostile band; both stay out of Critical.
        Assert.True(evidence.PrimaryBotType is BotType.GoodBot or BotType.SearchEngine,
            $"expected friendly type (GoodBot or SearchEngine), got {evidence.PrimaryBotType}");
        Assert.NotEqual(ThreatBand.Critical, evidence.ThreatBand);
    }

    /// <summary>
    ///     Builds a ledger with the minimal signal set the composer needs to
    ///     fire <see cref="Risk.SignatureRiskVerdict.HostilePinFired"/>: the
    ///     reputation fast-abort latch on plus a UA-derived friendly bot
    ///     identity so the override has something non-trivial to demote.
    ///     A small positive contribution from another detector keeps the
    ///     coverage formula honest.
    /// </summary>
    private static DetectionLedger BuildConfirmedBadLedger(
        BotType uaClaimedBotType, string uaClaimedBotName)
    {
        var ledger = new DetectionLedger("test-sig");

        // UA detector: matches Amazonbot pattern -> friendly GoodBot identity.
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.9,
            Reason = $"Known bot pattern: {uaClaimedBotName}",
            BotType = uaClaimedBotType.ToString(),
            BotName = uaClaimedBotName
        });

        // FastPathReputation detector: cache has latched ConfirmedBad on this
        // UA pattern. IsConfirmedBad(preSignals) reads ReputationFastAbortActive
        // + ReputationCanAbort off the MERGED contribution signals dictionary
        // (the per-contribution Signals map, not the ledger's free-floating
        // Record() store -- only contribution.Signals roll up into preSignals).
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "FastPathReputation",
            Category = "Reputation",
            ConfidenceDelta = 0.6,
            Reason = "Previously identified as bot (UserAgent seen 8 times)",
            Signals = new Dictionary<string, object>
            {
                [SignalKeys.ReputationFastAbortActive] = true,
                [SignalKeys.ReputationCanAbort] = true
            }
        });

        return ledger;
    }
}