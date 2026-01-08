using Mostlylucid.BotDetection.SignatureStore.Models;

namespace Mostlylucid.BotDetection.SignatureStore.Repositories;

/// <summary>
/// Repository for querying bot detection signatures from Postgres.
/// Supports efficient querying by any signal using JSONB GIN indexes.
/// </summary>
public interface ISignatureRepository
{
    /// <summary>
    /// Store a new signature (async, non-blocking)
    /// </summary>
    Task StoreSignatureAsync(SignatureEntity signature, CancellationToken cancellationToken = default);

    /// <summary>
    /// Store multiple signatures in a batch (for bulk operations)
    /// </summary>
    Task StoreBatchAsync(IEnumerable<SignatureEntity> signatures, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get recent signatures ordered by timestamp
    /// </summary>
    Task<List<SignatureQueryResult>> GetRecentAsync(int count = 100, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get top signatures ordered by bot probability
    /// </summary>
    Task<List<SignatureQueryResult>> GetTopByBotProbabilityAsync(
        int count = 100,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get signatures ordered by any JSONB signal path
    /// Examples:
    /// - "bot_probability" (top-level field)
    /// - "signals.ua.bot_probability" (nested signal)
    /// - "signals.tls.ja3_hash" (nested string)
    /// </summary>
    Task<List<SignatureQueryResult>> GetOrderedBySignalAsync(
        string signalPath,
        bool descending = true,
        int count = 100,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Filter signatures where a signal exists and optionally matches a value
    /// Examples:
    /// - signalPath: "signals.ua.headless_detected", value: true
    /// - signalPath: "signals.ip.datacenter", value: "aws"
    /// </summary>
    Task<List<SignatureQueryResult>> GetBySignalFilterAsync(
        string signalPath,
        object? signalValue = null,
        int count = 100,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get signatures by risk band
    /// </summary>
    Task<List<SignatureQueryResult>> GetByRiskBandAsync(
        string riskBand,
        int count = 100,
        int offset = 0,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Get a single signature by ID (with full JSON)
    /// </summary>
    Task<SignatureEntity?> GetByIdAsync(string signatureId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Get signature statistics (count by risk band, average bot probability, etc.)
    /// </summary>
    Task<SignatureStats> GetStatsAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete expired signatures (for cleanup/TTL)
    /// </summary>
    Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete signatures older than a given date
    /// </summary>
    Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default);
}

/// <summary>
/// Statistics for signature store
/// </summary>
public class SignatureStats
{
    public long TotalSignatures { get; set; }
    public Dictionary<string, long> CountByRiskBand { get; set; } = new();
    public double AverageBotProbability { get; set; }
    public double AverageConfidence { get; set; }
    public DateTime? OldestSignature { get; set; }
    public DateTime? NewestSignature { get; set; }
    public long ExpiredSignatures { get; set; }
}
