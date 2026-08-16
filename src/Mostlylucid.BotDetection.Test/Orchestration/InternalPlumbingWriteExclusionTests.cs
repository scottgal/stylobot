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
///     Pins the aggregation-side exclusion (2026-08-16, the hub-misclassification fix):
///     an Internal verdict — LAN-trusted peer, health probe, or the product's OWN
///     hub/beacon plumbing — must NEVER blend into the fingerprint's cached probability
///     via the orchestrator's per-request write-behind. The band clamp fixes the
///     per-request display; this skip fixes the SIGNATURE risk band, which derives
///     from fp.CachedBotProbability at read. Without it, the dashboard's own hub
///     connections (keyless, real browser UA) dragged their high probabilities into
///     the signature and flipped its band to High.
/// </summary>
public class InternalPlumbingWriteExclusionTests
{
    private const string FingerprintId = "fp-internal-plumbing-test";

    /// <summary>
    ///     Store double that records every <see cref="RecordVerdictWriteBehind"/>
    ///     the orchestrator makes — the same re-mapping trick as
    ///     <see cref="LearningSuppressionApiKeyTests"/>: the plain public method wins
    ///     over the interface default when called through the IFingerprintStore
    ///     reference the orchestrator holds (its WithPower default routes here).
    /// </summary>
    private sealed class RecordingFingerprintStore : NullFingerprintStore, IFingerprintStore
    {
        public List<string> VerdictWrites { get; } = new();

        public void RecordVerdictWriteBehind(string fingerprintId, double botProbability, string? botType = null)
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

    /// <summary>Raises request.internal_plumbing:true — stands in for InternalPlumbingAtom.</summary>
    private sealed class SeedInternalPlumbingAtom : DetectorAtomBase
    {
        public SeedInternalPlumbingAtom() : base("SeedInternalPlumbing", "Test") { }

        public override int Priority => 1;
        public override IReadOnlyList<string> RequiredSignals => Array.Empty<string>();

        public override Task<IReadOnlyList<DetectionContribution>> DetectAsync(
            SignalSink sink, string sessionId, CancellationToken ct = default)
        {
            sink.Raise($"{SignalKeys.InternalPlumbing}:true", sessionId);
            return Task.FromResult(None());
        }
    }

    private static (BotDetectionOrchestrator orchestrator, RecordingFingerprintStore store) BuildOrchestrator(
        bool raisePlumbingSignal)
    {
        var options = Options.Create(new BotDetectionOptions());

        var services = new ServiceCollection();
        services.AddSingleton<IDetectorAtom, SeedFingerprintIdAtom>();
        if (raisePlumbingSignal)
            services.AddSingleton<IDetectorAtom, SeedInternalPlumbingAtom>();
        var provider = services.BuildServiceProvider();

        var engine = new DetectionEngine(provider, options, NullLogger<DetectionEngine>.Instance);

        var store = new RecordingFingerprintStore();
        var orchestrator = new BotDetectionOrchestrator(
            engine, options, store, NullLogger<BotDetectionOrchestrator>.Instance);

        return (orchestrator, store);
    }

    [Fact]
    public async Task Internal_plumbing_verdict_never_writes_back_to_the_fingerprint()
    {
        var (orchestrator, store) = BuildOrchestrator(raisePlumbingSignal: true);

        await orchestrator.DetectAsync(new DefaultHttpContext());

        Assert.Empty(store.VerdictWrites);
    }

    [Fact]
    public async Task Non_internal_verdict_still_writes_back_to_the_fingerprint()
    {
        var (orchestrator, store) = BuildOrchestrator(raisePlumbingSignal: false);

        await orchestrator.DetectAsync(new DefaultHttpContext());

        Assert.Single(store.VerdictWrites);
        Assert.Equal(FingerprintId, store.VerdictWrites[0]);
    }
}
