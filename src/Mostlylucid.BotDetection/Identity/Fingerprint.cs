namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Domain record for one fingerprint shape — centroid, weights, and the metadata that
///     describes how the shape has evolved. Mirrors a row in the <c>fingerprints</c> table.
/// </summary>
public sealed record Fingerprint
{
    public required string FingerprintId { get; init; }
    public required float[] Centroid { get; init; }
    public required int CentroidMaturity { get; init; }
    public required float[] Weights { get; init; }
    public required int MemberCount { get; init; }
    public required int ObservationCount { get; init; }
    public required int CorrectionCount { get; init; }
    public required DateTime FirstSeen { get; init; }
    public required DateTime LastSeen { get; init; }
    public required double Quality { get; init; }
    public string? ArchetypeOrigin { get; init; }
    public required string InferredClientType { get; init; }
    public required double InferredTypeConfidence { get; init; }
    public required DateTime InferredTypeChangedAt { get; init; }
    public double CachedBotProbability { get; init; }
    public string? CachedRiskBand { get; init; }
    public DateTime? CachedScoreUpdatedAt { get; init; }
}

/// <summary>
///     A candidate result from the index search — fingerprint id, the distance components that
///     drove its inclusion, and (lazily) the loaded fingerprint row.
/// </summary>
public sealed record FingerprintCandidate(
    string FingerprintId,
    double CentroidScore,
    double BestObsScore);
