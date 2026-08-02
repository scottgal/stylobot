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
using Mostlylucid.BotDetection.Posture;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;
using Xunit;

namespace Mostlylucid.BotDetection.Test.Orchestration;

/// <summary>
///     2026-08-02 license-enforcement prerequisite: <see cref="IDetectionPostureProvider.LearningEnabled"/>
///     must globally suppress the orchestrator's identity-verdict write-back
///     (<see cref="IFingerprintStore.RecordVerdictWriteBehindWithPower"/>), independent of and
///     orthogonal to the existing per-API-key <c>IsLearningSuppressedByApiKey</c> gate. Mirrors
///     <see cref="LearningSuppressionApiKeyTests"/>'s structure for the SAME write path.
/// </summary>
public class DetectionPostureLearningGateTests
{
    private const string FingerprintId = "fp-posture-learn-test";

    private sealed class FakePostureProvider : IDetectionPostureProvider
    {
        public bool LearningEnabled { get; init; } = true;
        public bool ForceLogOnlyPosture { get; init; }
    }

    /// <summary>Records every <see cref="IFingerprintStore.RecordVerdictWriteBehindWithPower"/> call.</summary>
    private sealed class RecordingFingerprintStore : NullFingerprintStore, IFingerprintStore
    {
        public List<string> VerdictWrites { get; } = new();

        public void RecordVerdictWriteBehindWithPower(
            string fingerprintId, double botProbability, double confidence, bool isDefinitive, string? botType = null)
            => VerdictWrites.Add(fingerprintId);
    }

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

    private static (BotDetectionOrchestrator orchestrator, RecordingFingerprintStore store) BuildOrchestrator(
        IDetectionPostureProvider postureProvider)
    {
        var options = Options.Create(new BotDetectionOptions());

        var services = new ServiceCollection();
        services.AddSingleton<IDetectorAtom, SeedFingerprintIdAtom>();
        var provider = services.BuildServiceProvider();

        var engine = new DetectionEngine(provider, options, NullLogger<DetectionEngine>.Instance);

        var store = new RecordingFingerprintStore();
        var orchestrator = new BotDetectionOrchestrator(
            engine, options, store, NullLogger<BotDetectionOrchestrator>.Instance, postureProvider);

        return (orchestrator, store);
    }

    [Fact]
    public async Task LearningEnabled_false_suppresses_the_verdict_writeback()
    {
        var (orchestrator, store) = BuildOrchestrator(new FakePostureProvider { LearningEnabled = false });

        await orchestrator.DetectAsync(new DefaultHttpContext());

        Assert.Empty(store.VerdictWrites);
    }

    [Fact]
    public async Task LearningEnabled_true_still_writes_the_verdict_writeback()
    {
        var (orchestrator, store) = BuildOrchestrator(new FakePostureProvider { LearningEnabled = true });

        await orchestrator.DetectAsync(new DefaultHttpContext());

        Assert.Single(store.VerdictWrites);
        Assert.Equal(FingerprintId, store.VerdictWrites[0]);
    }
}
