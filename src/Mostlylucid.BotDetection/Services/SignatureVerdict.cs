using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Immutable snapshot of a primary signature's per-fingerprint state as held in
///     <see cref="Mostlylucid.BotDetection.Orchestration.SignatureCoordinator"/>'s
///     sliding window. The verdict is the live source of truth for the
///     <c>SignatureVerdictGate</c>: it does NOT come from a parallel cache and is
///     always derived from whatever the coordinator currently holds.
/// </summary>
public sealed record SignatureVerdict
{
    public required string SignatureId { get; init; }

    /// <summary>EWMA / running bot probability across this signature's observed requests.</summary>
    public required double BotProbability { get; init; }

    /// <summary>Confidence in the bot probability; usually grows with sample size.</summary>
    public required double Confidence { get; init; }

    /// <summary>Latest risk band classification. May be <see cref="RiskBand.Unknown"/> if not yet observed.</summary>
    public RiskBand RiskBand { get; init; } = RiskBand.Unknown;

    /// <summary>Latest threat (intent) score, orthogonal to BotProbability.</summary>
    public double ThreatScore { get; init; }

    /// <summary>Total observed requests for this signature in the window.</summary>
    public int RequestCount { get; init; }

    /// <summary>When the coordinator last observed this signature. Used by the gate for freshness.</summary>
    public DateTime LastSeenUtc { get; init; }
}
