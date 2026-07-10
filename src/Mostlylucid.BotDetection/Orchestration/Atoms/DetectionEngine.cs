using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Atoms;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration.Atoms;

/// <summary>
///     Process-singleton detection engine: builds the <see cref="DetectorOrchestrator"/>
///     and registers every <see cref="IDetectorAtom"/> ONCE at construction, then serves
///     concurrent per-request <see cref="DetectAsync"/> calls.
///
///     <para>
///     <b>Why this exists.</b> <see cref="BotDetectionOrchestrator"/> used to be
///     <c>AddScoped</c> and rebuilt the <see cref="DetectorOrchestrator"/> + ran the
///     ~70-atom <c>Register()</c> wave/priority loop on EVERY request. Invisible at idle
///     (~1 build/request, 0.16% CPU), but under concurrency it's a per-request wiring +
///     allocation storm that collapses the latency tail to the request timeout while the
///     median stays fast (deploy- isolated load test, 2026-07-10: p50 15 ms, p95 5 s,
///     saturating in the tens of RPS). CLAUDE.md flagged this as "the pooling lever if
///     RPS-scale GC pressure appears" -- this is that lever.
///     </para>
///     <para>
///     <b>Why sharing is safe.</b> The registered atoms are DI singletons
///     (<c>AddSingleton&lt;IDetectorAtom&gt;</c>), so they are ALREADY shared across
///     concurrent requests today. Each request passes its OWN <see cref="SignalSink"/>
///     into <see cref="DetectAsync"/>, and the per-request evidence is the returned
///     <see cref="DetectionLedger"/>; the engine holds only the read-only wave/atom
///     structures built at construction. So hoisting it to a singleton changes the atom
///     concurrency situation not at all -- it only stops the per-request re-wiring.
///     </para>
/// </summary>
public sealed class DetectionEngine
{
    private readonly DetectorOrchestrator _orchestrator;

    public DetectionEngine(
        IServiceProvider serviceProvider,
        IOptions<BotDetectionOptions> options,
        ILogger<DetectionEngine> logger)
    {
        var opts = options.Value;

        _orchestrator = new DetectorOrchestrator(new DetectorOrchestratorOptions
        {
            ParallelWaveExecution = opts.ParallelDetection,
            EnableQuorumExit = opts.EnableQuorumExit,
            QuorumConfidenceThreshold = opts.QuorumConfidenceThreshold,
            Timeout = TimeSpan.FromMilliseconds(opts.TimeoutMs)
        });

        var count = 0;
        foreach (var atom in serviceProvider.GetServices<IDetectorAtom>().Where(d => d.IsEnabled))
        {
            _orchestrator.Register(atom);
            count++;
        }

        logger.LogInformation(
            "DetectionEngine built once with {Count} detector atoms (shared singleton across requests)",
            count);
    }

    /// <summary>
    ///     Run the detection waves against a caller-owned per-request
    ///     <paramref name="sink"/> and return the accumulated <see cref="DetectionLedger"/>.
    ///     Thread-safe for concurrent calls: the engine holds only the read-only wave/atom
    ///     structures; all per-request state lives in the passed sink and the returned ledger.
    /// </summary>
    public Task<DetectionLedger> DetectAsync(SignalSink sink, string sessionId, CancellationToken ct = default)
        => _orchestrator.DetectAsync(sink, sessionId, ct);
}
