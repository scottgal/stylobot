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

    /// <summary>
    ///     EWMA-smoothed fraction of recent matches that landed in the ambiguity band
    ///     (Pass 2 correction, rotation candidate, L1 confirm fail, allocation). High
    ///     values reveal boundary-probing — see task #42.
    /// </summary>
    public double AmbiguityPersistence { get; init; }

    /// <summary>
    ///     Human-readable display name. Generated once at allocation via
    ///     <c>FingerprintNameComposer.Compose</c> from the matched archetype + UA
    ///     characterization. Updated only when drift exceeds <c>Match.SignificantDriftEpsilon</c>.
    ///     Empty for rows migrated from before the column existed; the matcher backfills on
    ///     next match. The contract elsewhere is "every fingerprint always has a name" — this
    ///     field's default is empty only to support migration, never as a runtime steady state.
    /// </summary>
    public string DisplayName { get; init; } = "";

    /// <summary>
    ///     UTC timestamp when <see cref="DisplayName"/> was last computed. Used by the
    ///     significant-drift path to decide whether enough has changed to warrant a recompute,
    ///     and surfaced to the dashboard as a freshness signal.
    /// </summary>
    public DateTime DisplayNameUpdatedAt { get; init; }

    /// <summary>
    ///     The reference centroid that drift is measured against. Seeded at allocation
    ///     from the matched archetype's centroid -- archetypes ARE the cold-start root,
    ///     not a placeholder waiting for "real" data. Replaced by <c>BotClusterService</c>
    ///     snapshots when the fingerprint's cluster produces a data-driven community mean,
    ///     so a Chrome-142 release that shifts the population's centroids gets reflected
    ///     in every member fingerprint's root within one clustering cycle even though
    ///     the seed archetype YAML is now stale. Every change writes a row to
    ///     <c>fingerprint_root_history</c> so the dashboard can show the evolution chain.
    ///     Nullable only on the migration boundary -- legacy rows are backfilled on
    ///     first startup. Runtime steady state: never null. A null at request time is a
    ///     bug, not a "calibrating" state.
    /// </summary>
    public float[]? RootCentroid { get; init; }

    /// <summary>UTC timestamp when <see cref="RootCentroid"/> was last set.</summary>
    public DateTime? RootCentroidAt { get; init; }

    /// <summary>
    ///     Lineage marker for <see cref="RootCentroid"/>: <c>archetype:&lt;id&gt;</c> at
    ///     allocation, <c>cluster:&lt;id&gt;</c> after a BotClusterService snapshot,
    ///     <c>verifiedbot:&lt;name&gt;</c> for the verifiedbot allocation path,
    ///     <c>bootstrap</c> for legacy rows backfilled on the migration boundary.
    /// </summary>
    public string? RootSource { get; init; }
}

/// <summary>
///     A candidate result from the index search — fingerprint id, the distance components that
///     drove its inclusion, and (lazily) the loaded fingerprint row.
/// </summary>
public sealed record FingerprintCandidate(
    string FingerprintId,
    double CentroidScore,
    double BestObsScore);

/// <summary>
///     A detailed observation that has met an absorption threshold; carries everything the
///     absorption transaction needs without re-reading.
/// </summary>
public sealed record AbsorbableObservation
{
    public required long ObservationId { get; init; }
    public required string FingerprintId { get; init; }
    public required float[] Vector { get; init; }
    public required float[] Centroid { get; init; }
    public required int CentroidMaturity { get; init; }
    public required float[] Weights { get; init; }
    public required string InferredClientType { get; init; }
}
