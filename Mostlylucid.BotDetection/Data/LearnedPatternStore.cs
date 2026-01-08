using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     SQLite-backed store for learned bot patterns.
///     Supports efficient querying by signature type, pattern matching, and confidence.
///     Schema design:
///     - Separate indexed columns for common query filters (type, pattern, confidence)
///     - JSON column for flexible metadata storage
///     - Regex patterns stored as-is for UA matching
///     - CIDR patterns for IP matching done in-memory after loading
/// </summary>
public interface ILearnedPatternStore
{
    /// <summary>Add or update a learned pattern</summary>
    Task UpsertAsync(LearnedSignature signature, CancellationToken ct = default);

    /// <summary>Get patterns by signature type</summary>
    Task<IReadOnlyList<LearnedSignature>> GetByTypeAsync(string signatureType, CancellationToken ct = default);

    /// <summary>Get all patterns above a confidence threshold</summary>
    Task<IReadOnlyList<LearnedSignature>> GetByConfidenceAsync(double minConfidence, CancellationToken ct = default);

    /// <summary>Get a specific pattern by ID</summary>
    Task<LearnedSignature?> GetAsync(string patternId, CancellationToken ct = default);

    /// <summary>Delete a pattern</summary>
    Task DeleteAsync(string patternId, CancellationToken ct = default);

    /// <summary>Get patterns that need feedback (enough occurrences)</summary>
    Task<IReadOnlyList<LearnedSignature>> GetPendingFeedbackAsync(int minOccurrences, CancellationToken ct = default);

    /// <summary>Mark pattern as fed back</summary>
    Task MarkFedBackAsync(string patternId, CancellationToken ct = default);

    /// <summary>Cleanup old patterns</summary>
    Task CleanupOldPatternsAsync(TimeSpan maxAge, CancellationToken ct = default);

    /// <summary>Get statistics</summary>
    Task<PatternStoreStats> GetStatsAsync(CancellationToken ct = default);
}

/// <summary>
///     Statistics about the pattern store.
/// </summary>
public class PatternStoreStats
{
    public int TotalPatterns { get; init; }
    public int UserAgentPatterns { get; init; }
    public int IpPatterns { get; init; }
    public int BehaviorPatterns { get; init; }
    public int PendingFeedback { get; init; }
    public int FedBack { get; init; }
    public DateTimeOffset? OldestPattern { get; init; }
    public DateTimeOffset? NewestPattern { get; init; }
}

/// <summary>
///     SQLite implementation of the learned pattern store.
/// </summary>
public class SqliteLearnedPatternStore : ILearnedPatternStore, IAsyncDisposable
{
    private const string TableName = "learned_patterns";
    private readonly string _connectionString;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<SqliteLearnedPatternStore> _logger;
    private bool _initialized;

    public SqliteLearnedPatternStore(
        ILogger<SqliteLearnedPatternStore> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;

        var dbPath = options.Value.DatabasePath
                     ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");

        _connectionString = $"Data Source={dbPath}";
    }

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }

    public async Task UpsertAsync(LearnedSignature signature, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            INSERT INTO {TableName} (
                pattern_id, signature_type, pattern, confidence, occurrences,
                first_seen, last_seen, action, bot_type, bot_name, source, metadata
            ) VALUES (
                @patternId, @signatureType, @pattern, @confidence, @occurrences,
                @firstSeen, @lastSeen, @action, @botType, @botName, @source, @metadata
            )
            ON CONFLICT(pattern_id) DO UPDATE SET
                confidence = MAX(confidence, @confidence),
                occurrences = occurrences + 1,
                last_seen = @lastSeen,
                action = @action,
                metadata = @metadata
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@patternId", signature.PatternId);
        cmd.Parameters.AddWithValue("@signatureType", signature.SignatureType);
        cmd.Parameters.AddWithValue("@pattern", signature.Pattern);
        cmd.Parameters.AddWithValue("@confidence", signature.Confidence);
        cmd.Parameters.AddWithValue("@occurrences", signature.Occurrences);
        cmd.Parameters.AddWithValue("@firstSeen", signature.FirstSeen.ToString("O"));
        cmd.Parameters.AddWithValue("@lastSeen", signature.LastSeen.ToString("O"));
        cmd.Parameters.AddWithValue("@action", signature.Action.ToString());
        cmd.Parameters.AddWithValue("@botType", signature.BotType?.ToString() ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@botName", signature.BotName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@source", signature.Source ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("@metadata", (object?)null ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LearnedSignature>> GetByTypeAsync(string signatureType,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            SELECT * FROM {TableName}
            WHERE signature_type = @type
            ORDER BY confidence DESC, occurrences DESC
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@type", signatureType);

        return await ReadSignaturesAsync(cmd, ct);
    }

    public async Task<IReadOnlyList<LearnedSignature>> GetByConfidenceAsync(double minConfidence,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            SELECT * FROM {TableName}
            WHERE confidence >= @minConfidence
            ORDER BY confidence DESC, occurrences DESC
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@minConfidence", minConfidence);

        return await ReadSignaturesAsync(cmd, ct);
    }

    public async Task<LearnedSignature?> GetAsync(string patternId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"SELECT * FROM {TableName} WHERE pattern_id = @id";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", patternId);

        var results = await ReadSignaturesAsync(cmd, ct);
        return results.FirstOrDefault();
    }

    public async Task DeleteAsync(string patternId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"DELETE FROM {TableName} WHERE pattern_id = @id";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", patternId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<LearnedSignature>> GetPendingFeedbackAsync(int minOccurrences,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            SELECT * FROM {TableName}
            WHERE occurrences >= @minOccurrences
              AND fed_back = 0
            ORDER BY confidence DESC, occurrences DESC
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@minOccurrences", minOccurrences);

        return await ReadSignaturesAsync(cmd, ct);
    }

    public async Task MarkFedBackAsync(string patternId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $"UPDATE {TableName} SET fed_back = 1 WHERE pattern_id = @id";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@id", patternId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task CleanupOldPatternsAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge).ToString("O");

        var sql = $@"
            DELETE FROM {TableName}
            WHERE last_seen < @cutoff
              AND occurrences < 10
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var deleted = await cmd.ExecuteNonQueryAsync(ct);
        if (deleted > 0) _logger.LogInformation("Cleaned up {Count} old learned patterns", deleted);
    }

    public async Task<PatternStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN signature_type = 'UserAgent' THEN 1 ELSE 0 END) as ua_count,
                SUM(CASE WHEN signature_type = 'IP' THEN 1 ELSE 0 END) as ip_count,
                SUM(CASE WHEN signature_type = 'Behavior' THEN 1 ELSE 0 END) as behavior_count,
                SUM(CASE WHEN fed_back = 0 THEN 1 ELSE 0 END) as pending,
                SUM(CASE WHEN fed_back = 1 THEN 1 ELSE 0 END) as fed_back,
                MIN(first_seen) as oldest,
                MAX(last_seen) as newest
            FROM {TableName}
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
            return new PatternStoreStats
            {
                TotalPatterns = reader.GetInt32(0),
                UserAgentPatterns = reader.GetInt32(1),
                IpPatterns = reader.GetInt32(2),
                BehaviorPatterns = reader.GetInt32(3),
                PendingFeedback = reader.GetInt32(4),
                FedBack = reader.GetInt32(5),
                OldestPattern = reader.IsDBNull(6) ? null : DateTimeOffset.Parse(reader.GetString(6)),
                NewestPattern = reader.IsDBNull(7) ? null : DateTimeOffset.Parse(reader.GetString(7))
            };

        return new PatternStoreStats();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Create table with indexed columns for common queries
            var sql = $@"
                CREATE TABLE IF NOT EXISTS {TableName} (
                    pattern_id TEXT PRIMARY KEY,
                    signature_type TEXT NOT NULL,
                    pattern TEXT NOT NULL,
                    confidence REAL NOT NULL,
                    occurrences INTEGER NOT NULL DEFAULT 1,
                    first_seen TEXT NOT NULL,
                    last_seen TEXT NOT NULL,
                    action TEXT NOT NULL DEFAULT 'LogOnly',
                    bot_type TEXT,
                    bot_name TEXT,
                    source TEXT,
                    fed_back INTEGER NOT NULL DEFAULT 0,
                    metadata TEXT
                );

                CREATE INDEX IF NOT EXISTS idx_signature_type ON {TableName}(signature_type);
                CREATE INDEX IF NOT EXISTS idx_confidence ON {TableName}(confidence);
                CREATE INDEX IF NOT EXISTS idx_occurrences ON {TableName}(occurrences);
                CREATE INDEX IF NOT EXISTS idx_fed_back ON {TableName}(fed_back);
                CREATE INDEX IF NOT EXISTS idx_last_seen ON {TableName}(last_seen);
            ";

            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
            _logger.LogDebug("Learned pattern store initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static async Task<IReadOnlyList<LearnedSignature>> ReadSignaturesAsync(
        SqliteCommand cmd,
        CancellationToken ct)
    {
        var results = new List<LearnedSignature>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(new LearnedSignature
            {
                PatternId = reader.GetString(reader.GetOrdinal("pattern_id")),
                SignatureType = reader.GetString(reader.GetOrdinal("signature_type")),
                Pattern = reader.GetString(reader.GetOrdinal("pattern")),
                Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
                Occurrences = reader.GetInt32(reader.GetOrdinal("occurrences")),
                FirstSeen = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("first_seen"))),
                LastSeen = DateTimeOffset.Parse(reader.GetString(reader.GetOrdinal("last_seen"))),
                Action = Enum.TryParse<LearnedPatternAction>(
                    reader.GetString(reader.GetOrdinal("action")), out var action)
                    ? action
                    : LearnedPatternAction.LogOnly,
                BotType = reader.IsDBNull(reader.GetOrdinal("bot_type"))
                    ? null
                    : Enum.TryParse<BotType>(reader.GetString(reader.GetOrdinal("bot_type")), out var bt)
                        ? bt
                        : null,
                BotName = reader.IsDBNull(reader.GetOrdinal("bot_name"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("bot_name")),
                Source = reader.IsDBNull(reader.GetOrdinal("source"))
                    ? null
                    : reader.GetString(reader.GetOrdinal("source"))
            });

        return results;
    }
}