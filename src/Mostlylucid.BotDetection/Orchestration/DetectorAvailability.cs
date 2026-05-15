using Mostlylucid.BotDetection.Policies;

namespace Mostlylucid.BotDetection.Orchestration;

/// <summary>
///     Single source of truth for "is this contributor allowed to run under this policy".
///     Both <see cref="EphemeralDetectionOrchestrator"/> and <see cref="BlackboardOrchestrator"/>
///     call through here so the policy/foundation rules cannot drift apart silently.
///     See <c>docs/architecture/signal-contracts.md</c>.
/// </summary>
internal static class DetectorAvailability
{
    /// <summary>
    ///     Foundation contributors bypass the policy allowlist and the exclusion set entirely.
    ///     Classifiers must be in the policy's combined detector list (or the policy must be
    ///     "run all" with an empty list) and must not be excluded.
    /// </summary>
    public static bool IsAvailableUnder(
        IContributingDetector detector,
        DetectionPolicy policy,
        IReadOnlySet<string> policyDetectors)
    {
        if (detector is IFoundationContributor) return true;
        if (policyDetectors.Count > 0 && !policyDetectors.Contains(detector.Name)) return false;
        return !policy.ExcludedDetectors.Contains(detector.Name);
    }
}
