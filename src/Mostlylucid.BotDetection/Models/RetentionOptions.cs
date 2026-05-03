namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Data retention and behavioral compression policy.
///     Controls how long each data type is kept and at what vector resolution.
///     VectorCompactionService runs the nightly compaction job.
///
///     Compaction uses dynamic resolution adjustment (LOD-style):
///     - L0: full individual session vectors (up to MaxSessionsPerSignature per signature)
///     - L1: per-signature centroid (behavioral shape + velocity centroid)
///     - L2: per-cluster centroid (lowest-priority signatures in a cluster merged to one entry)
///
///     Priority = risk × recency × bot_probability × entity_bonus.
///     High-risk bots, entity-mapped identities, and recent visitors retain L0 longest.
/// </summary>
public sealed class RetentionOptions
{
    // ==========================================
    // Classic time-based retention
    // ==========================================

    /// <summary>How long to keep session records. Sessions older than this are purged. Default: 30 days.</summary>
    public TimeSpan SessionRetention { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long to keep detection event records. Default: 7 days.</summary>
    public TimeSpan DetectionRetention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long to keep learned pattern data. Default: 90 days.</summary>
    public TimeSpan PatternRetention { get; set; } = TimeSpan.FromDays(90);

    /// <summary>How long to keep cluster records. Default: 14 days.</summary>
    public TimeSpan ClusterRetention { get; set; } = TimeSpan.FromDays(14);

    /// <summary>How long to keep reputation cache entries. Default: 24 hours.</summary>
    public TimeSpan ReputationRetention { get; set; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     How long to retain time-series bucket rows (chart counters).
    ///     Buckets are the only data type that is truly deleted (no compaction needed).
    ///     Default: 365 days.
    /// </summary>
    public TimeSpan BucketRetention { get; set; } = TimeSpan.FromDays(365);

    // ==========================================
    // Size-based limits
    // ==========================================

    /// <summary>Maximum number of session records to keep in the store. 0 = unlimited. Default: 500,000.</summary>
    public int MaxSessionCount { get; set; } = 500_000;

    /// <summary>Maximum number of learned pattern records to keep. 0 = unlimited. Default: 100,000.</summary>
    public int MaxPatternCount { get; set; } = 100_000;

    // ==========================================
    // Behavioral compression (LOD compaction)
    // ==========================================

    /// <summary>
    ///     Maximum full-resolution (L0) sessions per signature before SQLite compaction triggers.
    ///     Older sessions are compacted into the signature's root_vector centroid.
    ///     Default: 20.
    /// </summary>
    public int MaxSessionsPerSignature { get; set; } = 20;

    /// <summary>
    ///     When the HNSW session vector index exceeds this count, L1 compaction begins.
    ///     Low-priority signatures with multiple vectors are collapsed to a single centroid.
    ///     Default: 50,000 vectors.
    /// </summary>
    public int HnswLevel1Threshold { get; set; } = 50_000;

    /// <summary>
    ///     When the HNSW index still exceeds this count after L1 compaction, L2 compaction begins.
    ///     Low-priority signatures in the same cluster are merged into a single cluster centroid.
    ///     Default: 100,000 vectors.
    /// </summary>
    public int HnswLevel2Threshold { get; set; } = 100_000;

    /// <summary>
    ///     Signatures below this priority score are L2-compaction candidates.
    ///     Priority = risk × recency × bot_probability × entity_bonus.
    ///     Default: 0.1.
    /// </summary>
    public double L2CompactionPriorityThreshold { get; set; } = 0.1;

    // ==========================================
    // Schedule
    // ==========================================

    /// <summary>
    ///     Local time of day at which the nightly cleanup job runs.
    ///     Format: "HH:mm". Default: "02:00".
    /// </summary>
    public string CleanupTime { get; set; } = "02:00";

    /// <summary>
    ///     Hour of day (UTC) to run the nightly compaction job.
    ///     Default: 3am UTC (low-traffic window).
    /// </summary>
    public int CompactionHourUtc { get; set; } = 3;
}
