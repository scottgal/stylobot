namespace Mostlylucid.BotDetection.SignatureStore;

/// <summary>
/// Configuration options for the SignatureStore feature.
/// </summary>
public class SignatureStoreOptions
{
    /// <summary>
    /// Configuration section name
    /// </summary>
    public const string SectionName = "BotDetection:SignatureStore";

    /// <summary>
    /// Enable/disable signature storage.
    /// Default: false (must opt-in)
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Postgres connection string.
    /// Can use environment variable substitution: ${POSTGRES_CONNECTION}
    /// </summary>
    public string ConnectionString { get; set; } = string.Empty;

    /// <summary>
    /// Retention period in days (0 = keep forever).
    /// Signatures older than this will be deleted by cleanup job.
    /// Default: 30 days
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>
    /// Batch size for bulk inserts.
    /// Default: 100
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Flush interval for batch writes (milliseconds).
    /// Default: 5000 (5 seconds)
    /// </summary>
    public int FlushIntervalMs { get; set; } = 5000;

    /// <summary>
    /// Enable automatic cleanup of expired signatures.
    /// Default: true
    /// </summary>
    public bool EnableAutoCleanup { get; set; } = true;

    /// <summary>
    /// Cleanup interval in hours.
    /// Default: 24 (once per day)
    /// </summary>
    public int CleanupIntervalHours { get; set; } = 24;

    /// <summary>
    /// Maximum number of signatures to keep (0 = unlimited).
    /// If exceeded, oldest signatures are deleted.
    /// Default: 0 (unlimited)
    /// </summary>
    public int MaxSignatures { get; set; } = 0;

    /// <summary>
    /// Enable SignalR real-time broadcasting.
    /// Default: true
    /// </summary>
    public bool EnableSignalR { get; set; } = true;

    /// <summary>
    /// SignalR hub path.
    /// Default: /hubs/signatures
    /// </summary>
    public string SignalRHubPath { get; set; } = "/hubs/signatures";

    /// <summary>
    /// Enable API endpoints for querying signatures.
    /// Default: true
    /// </summary>
    public bool EnableApiEndpoints { get; set; } = true;

    /// <summary>
    /// API endpoint base path.
    /// Default: /api/signatures
    /// </summary>
    public string ApiBasePath { get; set; } = "/api/signatures";

    /// <summary>
    /// Apply environment variable substitution to connection string.
    /// Example: "${POSTGRES_CONNECTION}" -> value from environment variable
    /// </summary>
    public string GetConnectionString()
    {
        if (string.IsNullOrEmpty(ConnectionString))
            return string.Empty;

        // Simple environment variable substitution
        var resolved = ConnectionString;
        var start = resolved.IndexOf("${");

        while (start >= 0)
        {
            var end = resolved.IndexOf("}", start);
            if (end < 0)
                break;

            var varName = resolved.Substring(start + 2, end - start - 2);
            var varValue = Environment.GetEnvironmentVariable(varName) ?? string.Empty;
            resolved = resolved.Substring(0, start) + varValue + resolved.Substring(end + 1);

            start = resolved.IndexOf("${");
        }

        return resolved;
    }
}
