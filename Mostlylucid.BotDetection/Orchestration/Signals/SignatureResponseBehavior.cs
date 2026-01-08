namespace Mostlylucid.BotDetection.Orchestration.Signals;

/// <summary>
///     Aggregated signature response behavior from analysis lanes.
///     Combines behavioral, spectral, and reputation scores into overall assessment.
/// </summary>
public sealed record SignatureResponseBehavior
{
    public required string Signature { get; init; }
    public required double Score { get; init; }
    public required double BehavioralScore { get; init; }
    public required double SpectralScore { get; init; }
    public required double ReputationScore { get; init; }
    public required int WindowSize { get; init; }
}