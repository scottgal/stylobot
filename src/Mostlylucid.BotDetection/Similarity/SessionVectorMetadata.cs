namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Metadata stored alongside each session vector entry in the similarity cache and SQLite.
///     Preserved through compaction; CompressionLevel tracks whether this is a raw session
///     (L0), a per-signature centroid (L1), or a per-cluster centroid (L2).
/// </summary>
public class SessionVectorMetadata
{
    public string Signature { get; set; } = string.Empty;
    public bool IsBot { get; set; }
    public double BotProbability { get; set; }
    public DateTimeOffset Timestamp { get; set; }

    /// <summary>
    ///     Velocity vector from the previous session for this signature (current - previous).
    ///     Null for first sessions or when previous session was unavailable.
    ///     Preserved through compaction as a velocity centroid (average drift direction).
    /// </summary>
    public float[]? VelocityVector { get; set; }

    /// <summary>Cached L2 magnitude of VelocityVector (0 when null).</summary>
    public float VelocityMagnitude { get; set; }

    /// <summary>Compression level: 0=full L0 session, 1=per-signature centroid, 2=per-cluster centroid.</summary>
    public int CompressionLevel { get; set; }

    /// <summary>Priority score used by VectorCompactionService to decide which entries to compress first.</summary>
    public double Priority { get; set; } = 1.0;

    /// <summary>Cluster ID if this entry belongs to a detected bot cluster (set during L2 compaction).</summary>
    public string? ClusterId { get; set; }

    /// <summary>
    ///     Per-dimension variance of the vectors that were compacted into this centroid.
    ///     Non-null only for L1/L2 centroid entries (CompressionLevel >= 1).
    ///     Used for Mahalanobis ghost matching: dimensions with low variance are
    ///     discriminative; deviations there are anomalous even if small.
    /// </summary>
    public float[]? VarianceVector { get; set; }

    /// <summary>
    ///     Frequency fingerprint: autocorrelation at 8 lag scales.
    ///     Captures temporal rhythm independent of behavioral path.
    ///     Two campaigns with the same crawl loop will score high similarity
    ///     here even if their Markov path has rotated.
    /// </summary>
    public float[]? FrequencyFingerprint { get; set; }

    /// <summary>
    ///     Drift vector: behavioral trajectory direction in 129-dim space.
    ///     Slope of linear regression over the most recent N session vectors.
    ///     Non-null when at least 3 sessions exist for this signature.
    /// </summary>
    public float[]? DriftVector { get; set; }
}
