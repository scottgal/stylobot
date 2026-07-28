using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     CONTRACT: sink-raised hints must reach <c>evidence.Signals</c> — the real
///     production read surface that post-detection consumers (enforcement gates,
///     risk verdict, threat band, rate-limit key, dashboard) inspect.
///
///     Root cause this pins: atoms emit ONLY via <c>sink.Raise</c> and never
///     populate <c>contribution.Signals</c>, so <c>ledger.MergedSignals</c> — and
///     therefore <c>evidence.Signals</c> — was empty of every sink-raised hint in
///     production. ~24 consumers read those keys and silently got nothing.
///
///     These assertions read <c>evidence.Signals[key]</c> directly (NOT a
///     sink-remerged probe dict as the BDF rig previously did), so they FAIL before
///     the <see cref="Atoms.SinkEvidenceReader.ProjectSinkSignals"/> projection is
///     wired into <see cref="DetectionLedgerExtensions.ToAggregatedEvidence"/> and
///     PASS after it. Each signal is raised exactly as its owning atom raises it
///     (bare presence vs composite <c>key:value</c>), so the decode is exercised
///     end to end. The seam is driven the way <c>BotDetectionOrchestrator</c> drives
///     it in production: <c>sink</c> passed, <c>premergedSignals: null</c>.
/// </summary>
public sealed class SinkToEvidenceSignalsContractTests
{
    private static SignalSink Sink() => new(maxCapacity: 256, maxAge: TimeSpan.FromMinutes(5));

    private static DetectionLedger LedgerWithOneContribution()
    {
        var ledger = new DetectionLedger("sink-to-evidence-contract");
        ledger.AddContribution(new DetectionContribution
        {
            DetectorName = "UserAgent",
            Category = "Identity",
            ConfidenceDelta = 0.3,
            Weight = 1.0,
            Reason = "seed contribution — no contribution.Signals (as in production)",
        });
        return ledger;
    }

    [Fact]
    public void GatingSignals_RaisedOnSink_ReachEvidenceSignals_ViaProductionPath()
    {
        var sink = Sink();

        // Raise each gating signal exactly as its owning atom raises it.
        sink.Raise(SignalKeys.FriendlyIpVerified, "s");                        // VerifiedBotInlineAtom — bare presence
        sink.Raise($"{SignalKeys.AttackDetected}:true", "s");                  // HaxxorAtom — composite bool
        sink.Raise($"{SignalKeys.IntentThreatScore}:0.82", "s");              // IntentAtom — composite numeric (string on sink)
        sink.Raise($"{SignalKeys.ReputationCanAbort}:true", "s");             // ReputationBiasAtom — composite bool
        sink.Raise($"{SignalKeys.PrimarySignature}:abc123def", "s");         // SignatureAtom — composite string
        sink.Raise(SignalKeys.VerifiedBotChecked, "s");                      // VerifiedBotInlineAtom — bare presence
        sink.Raise($"{SignalKeys.ProgrammaticFetchAttestation}:true", "s");  // HeaderAtom — composite bool (the "bool vs string" double-break)

        // Production seam: sink passed, premergedSignals null.
        var evidence = LedgerWithOneContribution()
            .ToAggregatedEvidence(options: new BotDetectionOptions(), sink: sink);

        var signals = evidence.Signals;

        // 1. friendly.ip_verified — decoded to a real bool for the `is true` friendly-pin reader.
        Assert.True(signals.TryGetValue(SignalKeys.FriendlyIpVerified, out var friendly) && friendly is true,
            "friendly.ip_verified missing/not bool-true in evidence.Signals — verified-bot throttle exemption is dead.");

        // 2. attack.detected — bool true for the BlockResponseGate attack-block path.
        Assert.True(signals.TryGetValue(SignalKeys.AttackDetected, out var attack) && attack is true,
            "attack.detected missing/not bool-true — attack block path skipped.");

        // 3. intent.threat_score — present, and it must actually drive the threat band end to end.
        Assert.True(signals.ContainsKey(SignalKeys.IntentThreatScore),
            "intent.threat_score missing — threat band reads 0 on real threats.");
        Assert.True(evidence.ThreatScore >= 0.82,
            $"threat score did not flow from the projected string value (got {evidence.ThreatScore}).");
        Assert.True(evidence.ThreatBand >= ThreatBand.High,
            $"threat band not populated from the projected threat score (got {evidence.ThreatBand}).");

        // 4. reputation.can_abort — bool true for the ConfirmedBad latch.
        Assert.True(signals.TryGetValue(SignalKeys.ReputationCanAbort, out var canAbort) && canAbort is true,
            "reputation.can_abort missing/not bool-true — ConfirmedBad latch is dead.");

        // 5. signature.primary — string identity for signature bucketing (rate-limit key, block gate, challenge).
        Assert.True(signals.TryGetValue(SignalKeys.PrimarySignature, out var sig) && sig is "abc123def",
            "signature.primary missing/not the raised string — identity bucketing falls back to IP+UA.");

        // 6. verifiedbot.checked — presence (consumer uses ContainsKey to lift confidence 0.5 -> 1.0).
        Assert.True(signals.ContainsKey(SignalKeys.VerifiedBotChecked),
            "verifiedbot.checked missing — declared-bot confidence pinned at 0.5.");

        // 7. attestation.fetch_metadata — MUST be a real bool (consumer does `pfa is true`), not the string "true".
        Assert.True(signals.TryGetValue(SignalKeys.ProgrammaticFetchAttestation, out var pfa) && pfa is true,
            "attestation.fetch_metadata not bool-true — real-Chrome carve-out is double-broken.");
    }

    [Fact]
    public void ContributionBookkeepingSignals_AreNotFloodedIntoEvidenceSignals()
    {
        // The orchestrator raises contribution.* bookkeeping onto the same sink; those
        // are reconstructed via ReadContributions and must NOT pollute evidence.Signals.
        var sink = Sink();
        sink.Raise($"{SignalKeys.PrimarySignature}:sig", "s");
        sink.Raise("contribution.UserAgent.0:0.9|1.0|Tool|curl", "s");
        sink.Raise("risk.current_score:0.55", "s");

        var evidence = LedgerWithOneContribution()
            .ToAggregatedEvidence(options: new BotDetectionOptions(), sink: sink);

        Assert.True(evidence.Signals.ContainsKey(SignalKeys.PrimarySignature));
        Assert.DoesNotContain(evidence.Signals.Keys, k => k.StartsWith("contribution.", StringComparison.Ordinal));
    }
}
