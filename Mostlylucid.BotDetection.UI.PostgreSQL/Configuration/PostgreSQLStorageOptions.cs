namespace Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;

/// <summary>
/// Configuration options for PostgreSQL storage provider.
/// </summary>
public sealed class PostgreSQLStorageOptions
{
    /// <summary>
    /// PostgreSQL connection string.
    /// Required.
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Whether to automatically initialize the database schema on startup.
    /// Default: true
    /// </summary>
    public bool AutoInitializeSchema { get; set; } = true;

    /// <summary>
    /// Maximum number of detections to return in a single query.
    /// Default: 10000
    /// </summary>
    public int MaxDetectionsPerQuery { get; set; } = 10000;

    /// <summary>
    /// Maximum age of detections to keep in the database (in days).
    /// Older records will be purged automatically.
    /// Set to 0 to disable automatic purging.
    /// Default: 30 days
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Whether to enable automatic cleanup of old records.
    /// Default: true
    /// </summary>
    public bool EnableAutomaticCleanup { get; set; } = true;

    /// <summary>
    /// How often to run cleanup job (in hours).
    /// Default: 24 hours
    /// </summary>
    public int CleanupIntervalHours { get; set; } = 24;

    /// <summary>
    /// Command timeout in seconds for database operations.
    /// Default: 30 seconds
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Whether to use GIN index optimizations for signature searches.
    /// Requires pg_trgm extension.
    /// Default: true
    /// </summary>
    public bool UseGinIndexOptimizations { get; set; } = true;

    /// <summary>
    /// Whether to enable TimescaleDB optimizations.
    /// Requires TimescaleDB extension installed.
    /// Provides hypertables, compression, continuous aggregates, and retention policies.
    /// Default: false (enable for production high-scale deployments)
    /// </summary>
    public bool EnableTimescaleDB { get; set; } = false;

    /// <summary>
    /// TimescaleDB chunk interval for hypertable partitioning.
    /// Default: 1 day (good for most use cases)
    /// </summary>
    public TimeSpan TimescaleChunkInterval { get; set; } = TimeSpan.FromDays(1);

    /// <summary>
    /// Age of data before compression (TimescaleDB only).
    /// Default: 7 days
    /// </summary>
    public TimeSpan CompressionAfter { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    /// Continuous aggregate refresh interval (TimescaleDB only).
    /// Default: 30 seconds (for real-time dashboards)
    /// </summary>
    public TimeSpan AggregateRefreshInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether to enable pgvector for ML-based signature similarity search.
    /// Requires pgvector extension installed.
    /// Provides vector embeddings for semantic similarity matching (replaces Qdrant).
    /// Default: false (future feature)
    /// </summary>
    public bool EnablePgVector { get; set; } = false;

    /// <summary>
    /// Vector embedding dimension size.
    /// Common values:
    /// - 384: all-MiniLM-L6-v2 (fast, local)
    /// - 768: OpenAI ada-002
    /// - 1536: OpenAI text-embedding-3-small
    /// Default: 384
    /// </summary>
    public int VectorDimension { get; set; } = 384;

    /// <summary>
    /// HNSW index parameters for vector search performance tuning.
    /// m: Number of bi-directional links (higher = more accurate, more memory)
    /// Default: 16
    /// </summary>
    public int VectorIndexM { get; set; } = 16;

    /// <summary>
    /// HNSW index ef_construction parameter.
    /// Higher values = better index quality, slower build time.
    /// Default: 64
    /// </summary>
    public int VectorIndexEfConstruction { get; set; } = 64;

    /// <summary>
    /// Minimum similarity threshold for vector search (0-1).
    /// Default: 0.8 (80% similarity)
    /// </summary>
    public double VectorMinSimilarity { get; set; } = 0.8;
}
