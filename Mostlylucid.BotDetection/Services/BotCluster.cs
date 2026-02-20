namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Represents a discovered cluster of related bot signatures.
/// </summary>
public sealed record BotCluster
{
    /// <summary>Unique identifier for this cluster (hash-based).</summary>
    public required string ClusterId { get; init; }

    /// <summary>Classification of the cluster type.</summary>
    public BotClusterType Type { get; init; }

    /// <summary>Signature hashes belonging to this cluster.</summary>
    public required List<string> MemberSignatures { get; init; }

    /// <summary>Number of members in the cluster.</summary>
    public int MemberCount { get; init; }

    /// <summary>Average bot probability across all cluster members.</summary>
    public double AverageBotProbability { get; init; }

    /// <summary>Average pairwise similarity within the cluster (connected pairs only).</summary>
    public double AverageSimilarity { get; init; }

    /// <summary>Fraction of possible pairs that are connected (above similarity threshold). 1.0 = fully connected.</summary>
    public double Connectedness { get; init; }

    /// <summary>How tightly clustered in time (0.0 = spread out, 1.0 = all active simultaneously).</summary>
    public double TemporalDensity { get; init; }

    /// <summary>Most common country code in the cluster.</summary>
    public string? DominantCountry { get; init; }

    /// <summary>Most common ASN in the cluster.</summary>
    public string? DominantAsn { get; init; }

    /// <summary>Auto-generated label describing the cluster behavior.</summary>
    public string? Label { get; init; }

    /// <summary>LLM-generated description of the cluster behavior and intent. Null until LLM processes it.</summary>
    public string? Description { get; init; }

    /// <summary>When the earliest member was first seen.</summary>
    public DateTimeOffset FirstSeen { get; init; }

    /// <summary>When any member was last seen.</summary>
    public DateTimeOffset LastSeen { get; init; }
}

/// <summary>
///     Classification of bot cluster types.
/// </summary>
public enum BotClusterType
{
    /// <summary>Unclassified cluster.</summary>
    Unknown,

    /// <summary>Same behavioral fingerprint across different IPs = same bot software.</summary>
    BotProduct,

    /// <summary>Temporally correlated different behaviors = coordinated campaign.</summary>
    BotNetwork,

    /// <summary>Emergent cluster: bots grouped by community detection but not yet meeting
    /// the strict thresholds for BotProduct or BotNetwork. Still valuable for monitoring.</summary>
    Emergent
}
