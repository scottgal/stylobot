namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     A resolved actor identity. Multiple PrimarySignatures can map to one entity (merge).
///     One signature can fork into multiple entities (split). Entity assignment is mutable;
///     session snapshots are immutable truth that can always be replayed.
/// </summary>
public sealed record ResolvedEntity
{
    public required string EntityId { get; init; }
    public required DateTime CreatedAt { get; init; }
    public required DateTime UpdatedAt { get; init; }

    /// <summary>Identity confidence level (L0=Infrastructure → L5=Persistent Actor).</summary>
    public int ConfidenceLevel { get; init; }

    /// <summary>Number of identity factors resolved (IP, UA, TLS, H2, client, behavioral, etc.).</summary>
    public int FactorCount { get; init; } = 1;

    public bool IsBot { get; init; }
    public double BotProbability { get; init; }
    public double ReputationScore { get; init; }

    /// <summary>JSON: header/factor hashes that are stable for this entity (high PersonalStability × GlobalRarity).</summary>
    public string? StableAnchorsJson { get; init; }

    /// <summary>Detected rotation period in seconds (null if no rotation detected).</summary>
    public double? RotationCadenceSeconds { get; init; }

    /// <summary>Variance of inter-session velocity (low = systematic rotation).</summary>
    public double? VelocityVariance { get; init; }

    /// <summary>Arbitrary metadata.</summary>
    public string? MetadataJson { get; init; }
}

/// <summary>
///     Links a PrimarySignature to an entity with full audit trail.
///     Edges are created on merge, split, rewind, or initial assignment.
///     Reverted edges have RevertedAt set - they stay for audit but don't affect resolution.
/// </summary>
public sealed record EntityEdge
{
    public required string EdgeId { get; init; }
    public required string EntityId { get; init; }
    public required string Signature { get; init; }
    public required EntityEdgeType EdgeType { get; init; }
    public required double Confidence { get; init; }
    public required DateTime CreatedAt { get; init; }

    /// <summary>Human-readable reason: "cosine=0.91, markov_sim=0.95, timing_match=true"</summary>
    public string? Reason { get; init; }

    /// <summary>Set when this edge is reverted (rewind). Null = active edge.</summary>
    public DateTime? RevertedAt { get; init; }

    public bool IsActive => RevertedAt == null;
}

public enum EntityEdgeType
{
    /// <summary>First time this signature was seen - created a new entity.</summary>
    Initial,

    /// <summary>Signature merged into an existing entity (cosine neighbor + cadence match).</summary>
    Merge,

    /// <summary>Entity split into two (oscillation detected).</summary>
    Split,

    /// <summary>A previous merge was undone (post-merge divergence detected).</summary>
    Rewind,

    /// <summary>Two entities with parallel behavioral vectors flagged as related.</summary>
    Converge
}