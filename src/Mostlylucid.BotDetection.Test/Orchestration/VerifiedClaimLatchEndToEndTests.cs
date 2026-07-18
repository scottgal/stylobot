using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Identity;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;
using Mostlylucid.BotDetection.Orchestration.Atoms;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     REAL end-to-end guard for the verified-claim latch, driving the ACTUAL wired flow through the
///     REAL <see cref="SqliteFingerprintStore"/> and the REAL dashboard read-through projection --
///     NOT a recording double, and NOT a manual <c>UpdateClaimVerificationAsync</c> call.
///
///     The false-confidence gap that hid the prod bug (known bots reading VeryHigh) was exactly this:
///     the unit test manually called <c>UpdateClaimVerificationAsync</c> to SIMULATE a verification
///     flow that production never had, so it stayed green while no code path actually wrote the claim.
///     This test forces the whole chain: a request produces an
///     <see cref="EarlyExitVerdict.VerifiedGoodBot"/> early exit -> the orchestrator latches
///     <c>claim_status='verified'</c> on the real store -> a probability-1.0 fingerprint read back
///     through <see cref="SqliteFingerprintStore.GetResolvedVerdictsBySignaturesAsync"/> (the exact
///     projection the dashboard consumes) derives <c>Low</c>, not <c>VeryHigh</c>. If the wiring
///     regresses, the baseline assertion proves the read WOULD be VeryHigh without it.
/// </summary>
public class VerifiedClaimLatchEndToEndTests : IDisposable
{
    private const string PrimarySig = "sig-e2e-verified-goodbot";
    private const string FpId = "fp-e2e-verified-goodbot";
    private readonly string _tempDir;

    public VerifiedClaimLatchEndToEndTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sb-verified-latch-e2e-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Raises the identity fingerprint id + verification method and returns a VerifiedGoodBot early exit.</summary>
    private sealed class SeedVerifiedGoodBotAtom : DetectorAtomBase
    {
        public SeedVerifiedGoodBotAtom() : base("SeedVerifiedGoodBotE2E", "Test") { }
        public override int Priority => 1;
        public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();
        public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
            SignalSink sink, string sessionId, CancellationToken ct = default)
        {
            sink.Raise($"{SignalKeys.IdentityFingerprintId}:{FpId}", sessionId);
            sink.Raise($"{SignalKeys.VerifiedBotMethod}:ip_range", sessionId);
            return Task.FromResult(Single(
                DetectionContribution.VerifiedGoodBot("SeedVerifiedGoodBotE2E", "Verified test bot via ip_range", "Googlebot")));
        }
    }

    private async Task<SqliteFingerprintStore> NewStoreAsync(IOptions<BotDetectionOptions> options)
    {
        var store = new SqliteFingerprintStore(
            NullLogger<SqliteFingerprintStore>.Instance, options, IdentityVectorLayout.DefaultV1());
        await store.EnsureInitialisedAsync();
        return store;
    }

    private static Fingerprint NewFingerprint(int dim)
    {
        var now = DateTime.UtcNow;
        var weights = new float[dim];
        Array.Fill(weights, 1.0f);
        return new Fingerprint
        {
            FingerprintId = FpId,
            Centroid = new float[dim],
            CentroidMaturity = 1,
            Weights = weights,
            MemberCount = 1,
            ObservationCount = 12,
            CorrectionCount = 0,
            FirstSeen = now,
            LastSeen = now,
            Quality = 0.8,
            InferredClientType = "bot",
            InferredTypeConfidence = 1.0,
            InferredTypeChangedAt = now
        };
    }

    [Fact]
    public async Task VerifiedGoodBot_request_latches_verified_and_read_derives_Low_notVeryHigh()
    {
        var options = Options.Create(new BotDetectionOptions
        {
            DatabasePath = Path.Combine(_tempDir, "botdetection.db"),
            Identity = new IdentityOptions { Enabled = true }
        });
        var store = await NewStoreAsync(options);

        // Allocate + resident-load a probability-1.0 fingerprint (the exact prod shape: a known bot
        // whose cached score is 100%). Not yet verified.
        await store.InsertFingerprintAsync(NewFingerprint(store.Layout.Dimension), PrimarySig, CancellationToken.None);
        await store.GetFingerprintAsync(FpId, CancellationToken.None); // resident-load (mirrors the matcher)
        store.RecordVerdictWriteBehind(FpId, botProbability: 1.0, botType: BotType.SearchEngine.ToString());

        // BASELINE: without the verified latch, the real read-through projects VeryHigh -- proving this
        // test would actually catch a regression that failed to wire the latch.
        var baseline = await store.GetResolvedVerdictsBySignaturesAsync(new[] { PrimarySig }, CancellationToken.None);
        baseline[PrimarySig].IsVerifiedBot.Should().BeFalse();
        baseline[PrimarySig].RiskBand.Should().Be("VeryHigh");

        // Drive the REAL orchestrator against the REAL store. A VerifiedGoodBot early exit fires the
        // wired latch (no manual UpdateClaimVerificationAsync).
        var services = new ServiceCollection();
        services.AddSingleton<IDetectorAtom, SeedVerifiedGoodBotAtom>();
        var provider = services.BuildServiceProvider();
        var engine = new DetectionEngine(provider, options, NullLogger<DetectionEngine>.Instance);
        using var orchestrator = new BotDetectionOrchestrator(
            engine, options, store, NullLogger<BotDetectionOrchestrator>.Instance);

        await orchestrator.DetectAsync(new DefaultHttpContext());

        // Keep the cached probability high so the assertion isolates the CLAIM latch as the cause of Low
        // (a verified good bot at probability 1.0 is precisely the operator bug).
        store.RecordVerdictWriteBehind(FpId, botProbability: 1.0, botType: BotType.SearchEngine.ToString());

        // The wired latch persisted claim_status='verified'; the real read-through now derives Low.
        var afterLatch = await store.GetResolvedVerdictsBySignaturesAsync(new[] { PrimarySig }, CancellationToken.None);
        afterLatch[PrimarySig].IsVerifiedBot.Should().BeTrue("the orchestrator latched claim_status='verified' via the wired VerifiedGoodBot path");
        afterLatch[PrimarySig].RiskBand.Should().Be("Low");
        afterLatch[PrimarySig].RiskBand.Should().NotBe("VeryHigh");
    }
}
