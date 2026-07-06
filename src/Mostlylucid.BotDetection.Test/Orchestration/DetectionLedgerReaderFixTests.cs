using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     Pins the production bug where <c>ledger.MergedSignals</c> is built
///     only from <c>contribution.Signals</c>, while atoms (IpAtom etc.) raise
///     signals via <c>sink.Raise</c> and never populate
///     <c>contribution.Signals</c>. The six <c>is true</c> checks in
///     <see cref="DetectionLedgerExtensions.ToAggregatedEvidence"/> were
///     silently false in production: IpIsLocal, UserAgentIsBot,
///     ReputationFastAbortActive, SecurityToolDetected.
///
///     Fix: thread <see cref="SignalSink"/> into <c>ToAggregatedEvidence</c>
///     and read bool signals sink-first, falling back to the
///     <c>premergedSignals</c> dict only for callers (tests) that hand-build
///     signals directly.
///
///     Also pins Task 3 (health-probe shape-AND-source classification):
///     - Loopback + probe UA + health-endpoint path -> Internal.
///     - Loopback + browser navigation shape + health-endpoint path -> NOT Internal
///       (shape guard: source alone must not grant Internal on a health path).
/// </summary>
public sealed class DetectionLedgerReaderFixTests
{
    [Fact]
    public void LoopbackSinkSignal_PromotesToInternal_ViaProductionPath()
    {
        // Arrange: sink has ip.is_local:true (as IpAtom raises in production).
        var sink = new SignalSink(maxCapacity: 128, maxAge: TimeSpan.FromMinutes(5));
        sink.Raise($"{SignalKeys.IpIsLocal}:true", "s");

        // A ledger that would otherwise classify as a bot (curl-ish Tool verdict).
        // No premergedSignals -> exercises the production path where signals live
        // only in the sink (IpAtom raises them there, not into contribution.Signals).
        var ledger = new DetectionLedger("test-sink-reader-fix");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.9,
            Weight = 1.0,
            Reason = "curl/8.5.0 - tool-pattern match",
        });

        // Act: no premergedSignals passed -> production path; sink carries IpIsLocal.
        var evidence = ledger.ToAggregatedEvidence(options: new BotDetectionOptions(), sink: sink);

        // Assert: the IpIsLocal sink signal must flip PrimaryBotType to Internal.
        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
    }

    /// <summary>
    ///     Task 3 — shape-AND-source classification.
    ///     Loopback + probe UA + health-endpoint path -> <see cref="BotType.Internal"/>.
    ///     A health probe from a trusted source with probe shape must classify as Internal
    ///     even when the raw UA-derived type would be Tool.
    /// </summary>
    [Fact]
    public void HealthProbe_LoopbackCurl_ClassifiesAsInternal()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpIsLocal]      = true,
            [SignalKeys.HealthEndpoint] = true,
            [SignalKeys.UserAgent]      = "curl/8.5.0",
        };

        var ledger = new DetectionLedger("test-health-probe-curl");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.85,
            Weight = 1.0,
            BotType = BotType.Tool.ToString(),
            Reason = "curl/8.5.0 - tool UA",
        });

        var evidence = ledger.ToAggregatedEvidence(
            premergedSignals: signals,
            options: new BotDetectionOptions());

        Assert.Equal(BotType.Internal, evidence.PrimaryBotType);
    }

    /// <summary>
    ///     Task 3 — shape guard.
    ///     Loopback + browser navigation shape + health-endpoint path -> NOT Internal.
    ///     Source (local IP) alone must NOT grant Internal when the request is
    ///     browser-shaped on a health endpoint. This prevents an on-network attacker
    ///     from getting a free pass by hitting /health from a trusted IP with a
    ///     real browser (Sec-Fetch-Mode: navigate).
    /// </summary>
    [Fact]
    public void HealthProbe_ShapeGuard_BrowserNavigation_NotInternal()
    {
        var signals = new Dictionary<string, object>
        {
            [SignalKeys.IpIsLocal]          = true,
            [SignalKeys.HealthEndpoint]     = true,
            [SignalKeys.UserAgent]          = "Mozilla/5.0 (Windows NT 10.0) AppleWebKit/537.36 Chrome/120",
            [SignalKeys.HeaderSecFetchMode] = "navigate",
        };

        var ledger = new DetectionLedger("test-health-probe-shape-guard");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.1,
            Weight = 1.0,
            Reason = "Chrome browser UA",
        });

        var evidence = ledger.ToAggregatedEvidence(
            premergedSignals: signals,
            options: new BotDetectionOptions());

        // Browser-navigation shape on a health path must NOT yield Internal,
        // even from a loopback IP. Shape confirms the classification, source alone does not.
        Assert.NotEqual(BotType.Internal, evidence.PrimaryBotType);
    }
}
