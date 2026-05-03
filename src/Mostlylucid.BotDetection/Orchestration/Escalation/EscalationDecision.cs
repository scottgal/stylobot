namespace Mostlylucid.BotDetection.Orchestration.Escalation;

/// <summary>
///     Escalation decision result.
/// </summary>
public sealed record EscalationDecision
{
    /// <summary>Should escalation happen?</summary>
    public required bool ShouldEscalate { get; init; }

    /// <summary>Priority for signature processing (0-100)</summary>
    public required int Priority { get; init; }

    /// <summary>Reason for decision</summary>
    public required string Reason { get; init; }

    /// <summary>Should operation be stored?</summary>
    public bool ShouldStore { get; init; }

    /// <summary>Should alert be emitted?</summary>
    public bool ShouldAlert { get; init; }
}