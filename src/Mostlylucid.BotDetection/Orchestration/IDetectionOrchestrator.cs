using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies;
using Mostlylucid.Ephemeral;
using Mostlylucid.Ephemeral.Atoms.Taxonomy.Ledger;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Common contract for detection orchestrators. Middleware injects this
///     so the active orchestrator (Blackboard or Ephemeral) can be swapped
///     via DI registration without touching the middleware.
/// </summary>
public interface IDetectionOrchestrator
{
    Task<AggregatedEvidence> DetectWithPolicyAsync(
        HttpContext httpContext,
        DetectionPolicy policy,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Subscribe to the orchestrator's global blackboard signal stream. Listeners run
    ///     synchronously on the thread that raises the signal: keep them fast and non-throwing.
    ///     Dispose the returned subscription to stop delivery.
    /// </summary>
    IDisposable SubscribeToSignals(Action<SignalEvent> listener);

    /// <summary>
    ///     Raise a signal onto the global sink. Intended for cross-host observability and tests;
    ///     detectors raise via their per-request blackboard.
    /// </summary>
    void RaiseSignalForObservability(string signal, string? key = null);
}
