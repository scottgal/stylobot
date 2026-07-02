namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Compliance status the gateway (the enforcement component) exposes over REST
///     so an upstream dashboard host can show pack + guardian state without owning
///     any compliance store or connection (feedback_upstream_owns_no_stylobot_state).
///     The available packs are static embedded reference data (identical on every
///     host), so only the ACTIVE pack id and the live guardian count are dynamic and
///     travel here; the remote provider resolves the full pack locally from the id.
/// </summary>
public sealed record ComplianceStatusDto
{
    public string? ActivePackId { get; init; }
    public string? ActivePackName { get; init; }
    public int GuardianCount { get; init; }
}