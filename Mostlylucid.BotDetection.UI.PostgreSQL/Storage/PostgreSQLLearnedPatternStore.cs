using Dapper;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Npgsql;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Storage;

/// <summary>
/// PostgreSQL implementation of learned pattern store using Dapper.
/// Provides durable storage for machine-learned bot patterns.
/// </summary>
public class PostgreSQLLearnedPatternStore : ILearnedPatternStore, IAsyncDisposable
{
    private readonly PostgreSQLStorageOptions _options;
    private readonly ILogger<PostgreSQLLearnedPatternStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public PostgreSQLLearnedPatternStore(
        PostgreSQLStorageOptions options,
        ILogger<PostgreSQLLearnedPatternStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new ArgumentException("ConnectionString is required", nameof(options));
        }
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            const string createTableSql = @"
                CREATE TABLE IF NOT EXISTS learned_patterns (
                    pattern_id TEXT PRIMARY KEY,
                    signature_type TEXT NOT NULL,
                    pattern TEXT NOT NULL,
                    confidence DOUBLE PRECISION NOT NULL DEFAULT 0.5,
                    occurrences INTEGER NOT NULL DEFAULT 1,
                    first_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    action TEXT NOT NULL DEFAULT 'ScoreOnly',
                    bot_type TEXT,
                    bot_name TEXT,
                    source TEXT,
                    fed_back BOOLEAN NOT NULL DEFAULT FALSE,
                    metadata JSONB
                );

                CREATE INDEX IF NOT EXISTS idx_learned_patterns_type ON learned_patterns(signature_type);
                CREATE INDEX IF NOT EXISTS idx_learned_patterns_confidence ON learned_patterns(confidence DESC);
                CREATE INDEX IF NOT EXISTS idx_learned_patterns_fed_back ON learned_patterns(fed_back);
                CREATE INDEX IF NOT EXISTS idx_learned_patterns_last_seen ON learned_patterns(last_seen DESC);
            ";

            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.ExecuteAsync(createTableSql, commandTimeout: _options.CommandTimeoutSeconds);

            _initialized = true;
            _logger.LogInformation("PostgreSQL learned pattern store initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task UpsertAsync(LearnedSignature signature, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            INSERT INTO learned_patterns (
                pattern_id, signature_type, pattern, confidence, occurrences,
                first_seen, last_seen, action, bot_type, bot_name, source
            ) VALUES (
                @PatternId, @SignatureType, @Pattern, @Confidence, @Occurrences,
                @FirstSeen, @LastSeen, @Action, @BotType, @BotName, @Source
            )
            ON CONFLICT (pattern_id) DO UPDATE SET
                confidence = GREATEST(learned_patterns.confidence, @Confidence),
                occurrences = learned_patterns.occurrences + 1,
                last_seen = @LastSeen,
                action = @Action
        ";

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.ExecuteAsync(sql, new
            {
                signature.PatternId,
                signature.SignatureType,
                signature.Pattern,
                signature.Confidence,
                signature.Occurrences,
                signature.FirstSeen,
                signature.LastSeen,
                Action = signature.Action.ToString(),
                BotType = signature.BotType?.ToString(),
                signature.BotName,
                signature.Source
            }, commandTimeout: _options.CommandTimeoutSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to upsert learned pattern: {PatternId}", signature.PatternId);
            throw;
        }
    }

    public async Task<IReadOnlyList<LearnedSignature>> GetByTypeAsync(string signatureType, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT pattern_id, signature_type, pattern, confidence, occurrences,
                   first_seen, last_seen, action, bot_type, bot_name, source
            FROM learned_patterns
            WHERE signature_type = @SignatureType
            ORDER BY confidence DESC, occurrences DESC
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var results = await connection.QueryAsync<LearnedPatternRow>(sql,
            new { SignatureType = signatureType },
            commandTimeout: _options.CommandTimeoutSeconds);

        return results.Select(MapToSignature).ToList();
    }

    public async Task<IReadOnlyList<LearnedSignature>> GetByConfidenceAsync(double minConfidence, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT pattern_id, signature_type, pattern, confidence, occurrences,
                   first_seen, last_seen, action, bot_type, bot_name, source
            FROM learned_patterns
            WHERE confidence >= @MinConfidence
            ORDER BY confidence DESC, occurrences DESC
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var results = await connection.QueryAsync<LearnedPatternRow>(sql,
            new { MinConfidence = minConfidence },
            commandTimeout: _options.CommandTimeoutSeconds);

        return results.Select(MapToSignature).ToList();
    }

    public async Task<LearnedSignature?> GetAsync(string patternId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT pattern_id, signature_type, pattern, confidence, occurrences,
                   first_seen, last_seen, action, bot_type, bot_name, source
            FROM learned_patterns
            WHERE pattern_id = @PatternId
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var result = await connection.QueryFirstOrDefaultAsync<LearnedPatternRow>(sql,
            new { PatternId = patternId },
            commandTimeout: _options.CommandTimeoutSeconds);

        return result != null ? MapToSignature(result) : null;
    }

    public async Task DeleteAsync(string patternId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = "DELETE FROM learned_patterns WHERE pattern_id = @PatternId";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.ExecuteAsync(sql, new { PatternId = patternId }, commandTimeout: _options.CommandTimeoutSeconds);
    }

    public async Task<IReadOnlyList<LearnedSignature>> GetPendingFeedbackAsync(int minOccurrences, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT pattern_id, signature_type, pattern, confidence, occurrences,
                   first_seen, last_seen, action, bot_type, bot_name, source
            FROM learned_patterns
            WHERE fed_back = FALSE AND occurrences >= @MinOccurrences
            ORDER BY confidence DESC, occurrences DESC
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var results = await connection.QueryAsync<LearnedPatternRow>(sql,
            new { MinOccurrences = minOccurrences },
            commandTimeout: _options.CommandTimeoutSeconds);

        return results.Select(MapToSignature).ToList();
    }

    public async Task MarkFedBackAsync(string patternId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = "UPDATE learned_patterns SET fed_back = TRUE WHERE pattern_id = @PatternId";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        await connection.ExecuteAsync(sql, new { PatternId = patternId }, commandTimeout: _options.CommandTimeoutSeconds);
    }

    public async Task CleanupOldPatternsAsync(TimeSpan maxAge, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            DELETE FROM learned_patterns
            WHERE last_seen < @CutoffDate AND occurrences < 10
        ";

        var cutoffDate = DateTimeOffset.UtcNow - maxAge;

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var deleted = await connection.ExecuteAsync(sql, new { CutoffDate = cutoffDate }, commandTimeout: _options.CommandTimeoutSeconds);

        if (deleted > 0)
        {
            _logger.LogInformation("Cleaned up {Count} old learned patterns older than {MaxAge}", deleted, maxAge);
        }
    }

    public async Task<PatternStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT
                COUNT(*) as TotalPatterns,
                COUNT(*) FILTER (WHERE signature_type = 'UserAgent') as UserAgentPatterns,
                COUNT(*) FILTER (WHERE signature_type = 'IP') as IpPatterns,
                COUNT(*) FILTER (WHERE signature_type = 'Behavior') as BehaviorPatterns,
                COUNT(*) FILTER (WHERE fed_back = FALSE) as PendingFeedback,
                COUNT(*) FILTER (WHERE fed_back = TRUE) as FedBack,
                MIN(first_seen) as OldestPattern,
                MAX(last_seen) as NewestPattern
            FROM learned_patterns
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var result = await connection.QueryFirstAsync<PatternStoreStats>(sql, commandTimeout: _options.CommandTimeoutSeconds);

        return result;
    }

    private static LearnedSignature MapToSignature(LearnedPatternRow row)
    {
        LearnedPatternAction action = LearnedPatternAction.ScoreOnly;
        if (!string.IsNullOrEmpty(row.action) && Enum.TryParse<LearnedPatternAction>(row.action, out var parsedAction))
        {
            action = parsedAction;
        }

        BotType? botType = null;
        if (!string.IsNullOrEmpty(row.bot_type) && Enum.TryParse<BotType>(row.bot_type, out var parsedBotType))
        {
            botType = parsedBotType;
        }

        return new LearnedSignature
        {
            PatternId = row.pattern_id,
            SignatureType = row.signature_type,
            Pattern = row.pattern,
            Confidence = row.confidence,
            Occurrences = row.occurrences,
            FirstSeen = row.first_seen,
            LastSeen = row.last_seen,
            Action = action,
            BotType = botType,
            BotName = row.bot_name,
            Source = row.source
        };
    }

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        return ValueTask.CompletedTask;
    }

    // Internal DTO for Dapper mapping
    private class LearnedPatternRow
    {
        public string pattern_id { get; set; } = string.Empty;
        public string signature_type { get; set; } = string.Empty;
        public string pattern { get; set; } = string.Empty;
        public double confidence { get; set; }
        public int occurrences { get; set; }
        public DateTimeOffset first_seen { get; set; }
        public DateTimeOffset last_seen { get; set; }
        public string? action { get; set; }
        public string? bot_type { get; set; }
        public string? bot_name { get; set; }
        public string? source { get; set; }
    }
}
