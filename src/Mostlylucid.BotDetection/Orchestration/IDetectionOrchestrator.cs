using Microsoft.AspNetCore.Http;
using Mostlylucid.BotDetection.Policies;
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
}
