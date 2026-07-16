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
///     Enforcement guard for the bypass-key learning-suppression contract:
///     a request whose <c>X-SB-Api-Key</c> resolves to an
///     <see cref="ApiKeyContext"/> with <see cref="ApiKeyContext.DisableLearningWrites"/>
///     set must score normally but must NOT write back into the identity
///     model. This pins the orchestrator's headline-verdict write
///     (<see cref="IFingerprintStore.RecordVerdictWriteBehind"/>): skipped when
///     the request is learning-suppressed, called when it is not.
///
///     Regression guard for the "designed-but-unwired" bug where
///     <see cref="Extensions.HttpContextExtensions.IsLearningSuppressedByApiKey"/>
///     existed but no learning-write path consulted it, so keyed debug /
///     monitoring traffic still poisoned the model.
/// </summary>
public class LearningSuppressionApiKeyTests
{
    private const string FingerprintId = "fp-learnsuppress-test";

    // The Items key GetApiKeyContext()/IsLearningSuppressedByApiKey() read from.
    private const string ApiKeyContextItemsKey = "BotDetection.ApiKeyContext";

    /// <summary>
    ///     Store double that records every <see cref="RecordVerdictWriteBehind"/>
    ///     the orchestrator makes. Declared as a plain public method (no
    ///     <c>new</c>) so that a call through the <see cref="IFingerprintStore"/>
    ///     reference the orchestrator holds dispatches here rather than to the
    ///     interface's default no-op (which <see cref="NullFingerprintStore"/>
    ///     inherits). Every other store write is dropped by the base.
    /// </summary>
    private sealed class RecordingFingerprintStore : NullFingerprintStore, IFingerprintStore
    {
        public List<string> VerdictWrites { get; } = new();

        // Re-listing IFingerprintStore on this class re-maps its members to this
        // class's public methods, so RecordVerdictWriteBehind here becomes the
        // interface implementation and wins over the interface default no-op
        // (which NullFingerprintStore inherits) when called through the
        // IFingerprintStore reference the orchestrator holds.
        public void RecordVerdictWriteBehind(string fingerprintId, double botProbability, string? botType = null)
            => VerdictWrites.Add(fingerprintId);
    }

    /// <summary>
    ///     Priority-1 atom that raises the identity fingerprint id into the sink
    ///     so the orchestrator's headline-verdict write path is reached. Stands
    ///     in for the real FingerprintMatchAtom without enabling the Identity
    ///     subsystem.
    /// </summary>
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

    private static (BotDetectionOrchestrator orchestrator, RecordingFingerprintStore store) BuildOrchestrator()
    {
        var options = Options.Create(new BotDetectionOptions());

        var services = new ServiceCollection();
        services.AddSingleton<IDetectorAtom, SeedFingerprintIdAtom>();
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
    public async Task DisableLearningWrites_key_suppresses_RecordVerdictWriteBehind()
    {
        var (orchestrator, store) = BuildOrchestrator();
        var context = ContextWithApiKey(disableLearningWrites: true);

        await orchestrator.DetectAsync(context);

        Assert.Empty(store.VerdictWrites);
    }

    [Fact]
    public async Task No_suppression_still_writes_RecordVerdictWriteBehind()
    {
        var (orchestrator, store) = BuildOrchestrator();
        var context = ContextWithApiKey(disableLearningWrites: false);

        await orchestrator.DetectAsync(context);

        Assert.Single(store.VerdictWrites);
        Assert.Equal(FingerprintId, store.VerdictWrites[0]);
    }
}
