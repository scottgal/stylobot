namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Visitor "flag as wrong" feedback on the Your Detection card. The upstream
///     site captures the click and POSTs this shape to the gateway; the gateway
///     (the enforcement component) owns the store and persists one row per flag so
///     the BDF can be replayed offline to understand the verdict. The upstream never
///     writes it directly — per feedback_upstream_owns_no_stylobot_state, all
///     StyloBot state lives on the enforcement component.
///     <para>
///     No PII: the IP is already hashed into the signature at the gateway before this
///     shape is built; only gateway-derived identifiers and the verdict shape travel.
///     </para>
/// </summary>
public sealed record DetectionFeedbackRecord
{
    public string? PrimarySignature { get; init; }
    public string? EntityId { get; init; }
    public string? FingerprintId { get; init; }
    public double? BotProbability { get; init; }
    public double? Confidence { get; init; }
    public string? RiskBand { get; init; }
    public string? BotName { get; init; }
    public string? BotType { get; init; }
    public string? CountryCode { get; init; }
    public string? UserAgent { get; init; }
    public string? Note { get; init; }
}