using System.Collections.Generic;
using System.Net;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Dashboard;
using Mostlylucid.BotDetection.Middleware;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.BotDetection.UI.Services;
using Xunit;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Pins in-process (non-remote-gateway) signature resolution for the dashboard header's
///     "Your Signature" panel/link. SignatureAtom writes the real, orchestrator-computed
///     signature two ways: the rich object into <c>context.Items[SignatureAtom.MultifactorKey]</c>,
///     and (sink-only, never mirrored into <see cref="AggregatedEvidence.Signals"/>) a
///     <c>signature.primary</c> hint. <see cref="DetectionDataExtractor.Extract"/> must resolve
///     the SAME signature the persistence layer keys fingerprints on, not degrade to the ad-hoc
///     SHA256 IP+UA fallback used only when no real detection ran.
/// </summary>
public class DetectionDataExtractorInProcessSignatureTests
{
    [Fact]
    public void Extract_resolves_the_real_orchestrator_signature_not_the_fallback_hash()
    {
        var context = new DefaultHttpContext();
        context.Request.Headers["User-Agent"] = "Mozilla/5.0 test-agent";
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.5");

        // What SignatureAtom actually writes in-process (see SignatureAtom.DetectAsync):
        // the rich MultiFactorSignatures object into Items[MultifactorKey].
        var realSignatures = new MultiFactorSignatures { PrimarySignature = "real-multifactor-sig-abc123" };
        context.Items[SignatureAtom.MultifactorKey] = realSignatures;

        // A realistic post-merge AggregatedEvidence: Signals does NOT contain
        // signature.primary, because SignatureAtom only ever sink.Raise()s it --
        // ledger.MergedSignals (and therefore AggregatedEvidence.Signals) never carries
        // sink-only hints in production (see the ReadBool/ReadString "sink-first"
        // comments in DetectionLedgerExtensions.ToAggregatedEvidence).
        var evidence = new AggregatedEvidence
        {
            BotProbability = 0.1,
            Confidence = 0.5,
            RiskBand = RiskBand.Low,
            Signals = new Dictionary<string, object>(),
        };
        context.Items[BotDetectionMiddleware.AggregatedEvidenceKey] = evidence;

        var extractor = new DetectionDataExtractor();
        var model = extractor.Extract(context);

        model.Signatures.Should().NotBeNull();
        model.Signatures!.PrimarySignature.Should().Be(
            "real-multifactor-sig-abc123",
            "the dashboard header's \"Your Signature\" link must resolve to the SAME signature " +
            "the orchestrator computed and persistence keys fingerprints on -- not a transient " +
            "SHA256 IP+UA fallback that was never stored anywhere");
    }
}
