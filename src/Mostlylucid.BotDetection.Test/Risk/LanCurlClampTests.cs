using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Risk;

/// <summary>
///     Tests for the curl-from-LAN / Tool-family verdict carve-out
///     (see <see cref="DetectionLedgerExtensions"/>).
///
///     Background — incident 2026-06-29 staging:
///     <code>curl https://staging.stylobot.net/...</code> returned 429 with
///     <c>X-Bot-Verdict: VerifiedBadBot</c>, <c>X-Bot-Risk-Score: 1.000</c>,
///     <c>X-Bot-Reason: Previously identified as bot (IP seen 116 times)</c>.
///     Root causes (four arms, all fixed together):
///
///     1. <c>FastPathReputationContributor</c> early-exited on any IP-pattern at
///        <c>ConfirmedBad</c> with no awareness of the contemporaneous UA-catalog
///        type.
///     2. <c>PatternReputation.EvaluateStateChange</c> promoted any IP/UA centroid
///        to <c>ConfirmedBad</c> on raw repeat-count alone.
///     3. <c>DetectionLedgerExtensions.CreateEarlyExitResult</c> had a friendly-bot
///        rescue arm but no Tool-family arm — so a fast-abort early-exit on a
///        Tool UA shipped <c>VerifiedBadBot</c> with no remediation.
///     4. <c>CreateEarlyExitResult</c> ignored <see cref="SignalKeys.IpIsLocal"/>,
///        while the non-early-exit composer clamps to <see cref="BotType.Internal"/>
///        on the same signal — asymmetry let LAN clients be slammed by the
///        early-exit path even though the non-early-exit path correctly clamped.
///
///     User principle: "No exemption rule; the detection is faulty. It should
///     know a local ip can use curl."
/// </summary>
public class LanCurlClampTests
{
    /// <summary>
    ///     Fixture B — curl from RFC1918 reaching a synthetic VerifiedBadBot
    ///     early-exit verdict (the IP-pattern latch). The new IpIsLocal arm
    ///     in CreateEarlyExitResult must clamp PrimaryBotType=Internal and
    ///     RiskBand=VeryLow even when the early-exit verdict says VerifiedBadBot.
    /// </summary>
    [Fact]
    public void CreateEarlyExitResult_LanIp_ClampsInternalEvenWhenVerdictIsVerifiedBadBot()
    {
        var ledger = new DetectionLedger("test-lan-curl-clamp");

        // Synthetic FastPath early-exit contribution — mimics the staging
        // incident where the IP-rep cache latched ConfirmedBad and emitted
        // a VerifiedBot early-exit.
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "FastPathReputation",
            Category = "Reputation",
            ConfidenceDelta = 1.0,
            Weight = 3.0,
            Reason = "Previously identified as bot (IP seen 116 times)",
            TriggerEarlyExit = true,
            EarlyExitVerdict = nameof(EarlyExitVerdict.VerifiedBadBot)
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpIsLocal] = true,
            [SignalKeys.UserAgent] = "curl/8.5.0",
            [SignalKeys.UserAgentBotName] = "curl",
            [SignalKeys.UserAgentBotType] = nameof(BotType.Tool),
            [SignalKeys.UserAgentIsBot] = true
        };

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.True(evidence.EarlyExit, "Early-exit path must still be taken");
        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.Equal(RiskBand.VeryLow, evidence.RiskBand);
    }

    /// <summary>
    ///     Fixture E — synthetic ledger with VerifiedBadBot early-exit AND
    ///     signals[IpIsLocal] = true must clamp Internal/VeryLow, regardless
    ///     of UA classification. Architectural symmetry fix: the non-early-
    ///     exit composer already does this for the same signal.
    /// </summary>
    [Fact]
    public void CreateEarlyExitResult_VerifiedBadBot_IpIsLocal_ClampsInternalVeryLow()
    {
        var ledger = new DetectionLedger("test-early-exit-symmetry");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "FastPathReputation",
            Category = "Reputation",
            ConfidenceDelta = 1.0,
            Weight = 3.0,
            Reason = "Previously identified as bot",
            TriggerEarlyExit = true,
            EarlyExitVerdict = nameof(EarlyExitVerdict.VerifiedBadBot)
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpIsLocal] = true,
        };

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
        Assert.Equal(RiskBand.VeryLow, evidence.RiskBand);
    }

    /// <summary>
    ///     Tool-family demotion arm on the early-exit path. When verdict is
    ///     VerifiedBadBot AND the resolved name resolves to a Tool-family
    ///     catalog entry (curl, wget, python-requests) AND there are NO
    ///     SecurityToolDetected / honeypot / hostile contributions, demote
    ///     to BotType.Tool + RiskBand.Elevated (not VeryHigh).
    /// </summary>
    [Fact]
    public void CreateEarlyExitResult_VerifiedBadBot_ToolUa_NoHostileSignals_DemotesToTool()
    {
        var ledger = new DetectionLedger("test-tool-demote");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "FastPathReputation",
            Category = "Reputation",
            ConfidenceDelta = 1.0,
            Weight = 3.0,
            Reason = "Previously identified as bot (IP seen 116 times)",
            TriggerEarlyExit = true,
            EarlyExitVerdict = nameof(EarlyExitVerdict.VerifiedBadBot)
        });

        // Public IP — no IpIsLocal — so the Internal clamp does NOT fire.
        // The Tool-family demote arm should still rescue this from VerifiedBadBot.
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = "curl/8.5.0",
            [SignalKeys.UserAgentBotName] = "curl",
            [SignalKeys.UserAgentBotType] = nameof(BotType.Tool),
            [SignalKeys.UserAgentIsBot] = true
        };

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.Equal(BotType.Tool, evidence.PrimaryBotType);
        Assert.True(evidence.RiskBand <= RiskBand.Elevated,
            $"Tool-family demote must cap at Elevated; got {evidence.RiskBand}");
    }

    /// <summary>
    ///     Regression guard: SecurityToolContributor's verdict path is NOT
    ///     weakened. When SecurityToolDetected=true (sqlmap, nikto, etc.),
    ///     the early-exit verdict must still be VerifiedBadBot at VeryHigh.
    /// </summary>
    [Fact]
    public void CreateEarlyExitResult_SecurityToolDetected_StaysVerifiedBadBot()
    {
        var ledger = new DetectionLedger("test-security-tool");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "SecurityTool",
            Category = "SecurityTool",
            ConfidenceDelta = 0.95,
            Weight = 2.0,
            Reason = "Security/hacking tool detected: sqlmap",
            TriggerEarlyExit = true,
            EarlyExitVerdict = nameof(EarlyExitVerdict.VerifiedBadBot)
        });

        var signals = new Dictionary<string, object>
        {
            [SignalKeys.UserAgent] = "sqlmap/1.7.2",
            [SignalKeys.UserAgentBotName] = "sqlmap",
            [SignalKeys.UserAgentBotType] = nameof(BotType.MaliciousBot),
            [SignalKeys.UserAgentIsBot] = true,
            [SignalKeys.SecurityToolDetected] = true,
            [SignalKeys.SecurityToolName] = "sqlmap"
        };

        var evidence = ledger.ToAggregatedEvidence(aiRan: false, premergedSignals: signals);

        Assert.Equal(EarlyExitVerdict.VerifiedBadBot, evidence.EarlyExitVerdict);
        Assert.Equal(RiskBand.VeryHigh, evidence.RiskBand);
    }
}
