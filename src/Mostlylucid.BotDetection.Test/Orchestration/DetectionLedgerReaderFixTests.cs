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
}
