using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
///     Enforcement guard for the durable "verified" latch wiring. THE prod bug: known good
///     bots (Googlebot/Bingbot) read "VeryHigh" risk on the dashboard because
///     <see cref="FingerprintRiskProjection"/> derives the band at read and its verified -> Low
///     friendly-pin fires ONLY on a stored <c>claim_status = 'verified'</c> -- but nothing in
///     production ever wrote that status. <see cref="IFingerprintStore.UpdateClaimVerificationAsync"/>
///     (the only writer) had ZERO production callers: the persistent-trust-state feature shipped the
///     store method + schema + tests, but the orchestration wiring to call it on a real verification
///     was never built, so every fingerprint stayed 'unverified' and every known bot bucketed VeryHigh.
///
///     The fix wires the confirmed-verification into a durable claim at the orchestrator's
///     verdict-persist seam: when a verification channel produces an
///     <see cref="EarlyExitVerdict.VerifiedGoodBot"/> early exit (the same spoof-safe predicate
///     <c>BlockResponseGate</c> uses -- spoofed UAs never produce it), latch
///     <c>claim_status = 'verified'</c>. These tests pin: (1) a VerifiedGoodBot request latches
///     verified; (2) a non-verified request does NOT; (3) a learning-suppressed request does NOT
///     (the latch is an identity write, gated with the headline write).
/// </summary>
public class IdentityVerifiedClaimLatchTests
{
    private const string FingerprintId = "fp-verified-latch-test";
    private const string ApiKeyContextItemsKey = "BotDetection.ApiKeyContext";

    /// <summary>
    ///     Store double recording both the headline write and the trust-claim latch. Re-lists
    ///     <see cref="IFingerprintStore"/> so its public methods win over the interface defaults
    ///     that <see cref="NullFingerprintStore"/> inherits (same trick as
    ///     <c>LearningSuppressionApiKeyTests.RecordingFingerprintStore</c>).
    /// </summary>
    private sealed class RecordingFingerprintStore : NullFingerprintStore, IFingerprintStore
    {
        public List<string> VerdictWrites { get; } = new();
        public List<(string Id, string Claim, string? Method)> ClaimLatches { get; } = new();

        public void RecordVerdictWriteBehind(string fingerprintId, double botProbability, string? botType = null)
            => VerdictWrites.Add(fingerprintId);

        public new Task UpdateClaimVerificationAsync(
            string fingerprintId, string claimStatus, string? verificationMethod,
            DateTime? verifiedAt, CancellationToken ct = default)
        {
            ClaimLatches.Add((fingerprintId, claimStatus, verificationMethod));
            return Task.CompletedTask;
        }
    }

    /// <summary>Seeds the fingerprint id; no verification (no early exit).</summary>
    private sealed class SeedFingerprintIdAtom : DetectorAtomBase
    {
        public SeedFingerprintIdAtom() : base("SeedFingerprintId", "Test") { }
        public override int Priority => 1;
        public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();
        public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
            SignalSink sink, string sessionId, CancellationToken ct = default)
        {
            sink.Raise($"{SignalKeys.IdentityFingerprintId}:{FingerprintId}", sessionId);
            return Task.FromResult(None());
        }
    }

    /// <summary>Seeds the fingerprint id + verification method and returns a VerifiedGoodBot early exit.</summary>
    private sealed class SeedVerifiedGoodBotAtom : DetectorAtomBase
    {
        public SeedVerifiedGoodBotAtom() : base("SeedVerifiedGoodBot", "Test") { }
        public override int Priority => 1;
        public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();
        public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
            SignalSink sink, string sessionId, CancellationToken ct = default)
        {
            sink.Raise($"{SignalKeys.IdentityFingerprintId}:{FingerprintId}", sessionId);
            sink.Raise($"{SignalKeys.VerifiedBotMethod}:ip_range", sessionId);
            return Task.FromResult(Single(
                DetectionContribution.VerifiedGoodBot("SeedVerifiedGoodBot", "Verified test bot via ip_range", "Googlebot")));
        }
    }

    private static (BotDetectionOrchestrator orchestrator, RecordingFingerprintStore store) BuildOrchestrator(
        IDetectorAtom seed)
    {
        var options = Options.Create(new BotDetectionOptions());
        var services = new ServiceCollection();
        services.AddSingleton(seed);
        var provider = services.BuildServiceProvider();
        var engine = new DetectionEngine(provider, options, NullLogger<DetectionEngine>.Instance);
        var store = new RecordingFingerprintStore();
        var orchestrator = new BotDetectionOrchestrator(
            engine, options, store, NullLogger<BotDetectionOrchestrator>.Instance);
        return (orchestrator, store);
    }

    private static DefaultHttpContext ContextWithApiKey(bool disableLearningWrites)
    {
        var context = new DefaultHttpContext();
        context.Items[ApiKeyContextItemsKey] = new ApiKeyContext
        {
            KeyName = "debug-key",
            DisabledDetectors = Array.Empty<string>(),
            WeightOverrides = new Dictionary<string, double>(),
            DisableLearningWrites = disableLearningWrites,
        };
        return context;
    }

    [Fact]
    public async Task VerifiedGoodBot_earlyExit_latches_claim_status_verified()
    {
        var (orchestrator, store) = BuildOrchestrator(new SeedVerifiedGoodBotAtom());

        await orchestrator.DetectAsync(new DefaultHttpContext());

        Assert.Single(store.ClaimLatches);
        Assert.Equal(FingerprintId, store.ClaimLatches[0].Id);
        Assert.Equal("verified", store.ClaimLatches[0].Claim);
        Assert.Equal("ip_range", store.ClaimLatches[0].Method);
    }

    [Fact]
    public async Task NonVerified_request_does_not_latch()
    {
        var (orchestrator, store) = BuildOrchestrator(new SeedFingerprintIdAtom());

        await orchestrator.DetectAsync(new DefaultHttpContext());

        Assert.Empty(store.ClaimLatches);
        Assert.Single(store.VerdictWrites); // headline write still happens
    }

    [Fact]
    public async Task LearningSuppressed_verifiedBot_does_not_latch()
    {
        var (orchestrator, store) = BuildOrchestrator(new SeedVerifiedGoodBotAtom());

        await orchestrator.DetectAsync(ContextWithApiKey(disableLearningWrites: true));

        Assert.Empty(store.ClaimLatches);
        Assert.Empty(store.VerdictWrites);
    }
}
