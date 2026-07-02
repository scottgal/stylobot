namespace Mostlylucid.BotDetection.Compliance;

/// <summary>
///     Read-only compliance runtime status a dashboard host can surface without
///     owning the compliance stack. On a remote-mode host this reads from the
///     gateway's compliance status endpoint — the guardians run on the enforcement
///     component, not here (feedback_upstream_owns_no_stylobot_state). Absent on
///     hosts with no compliance wiring, so consumers treat it as optional.
/// </summary>
public interface IComplianceStatusReader
{
    /// <summary>Number of active compliance guardians on the enforcement component.</summary>
    int GuardianCount { get; }
}