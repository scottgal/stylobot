using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Npgsql;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Storage;

/// <summary>
/// PostgreSQL implementation of the weight store with memory caching.
/// Uses a CQRS-style pattern with write-behind for performance.
/// </summary>
public class PostgreSQLWeightStore : IWeightStore, IAsyncDisposable, IDisposable
{
    private readonly PostgreSQLStorageOptions _options;
    private readonly ILogger<PostgreSQLWeightStore> _logger;
    private readonly BotDetectionMetrics? _metrics;
    private readonly MemoryCache _cache;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly ConcurrentQueue<PendingWrite> _pendingWrites = new();
    private readonly Timer _flushTimer;
    private readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(500);
    private bool _initialized;
    private bool _disposed;

    public PostgreSQLWeightStore(
        PostgreSQLStorageOptions options,
        ILogger<PostgreSQLWeightStore> logger,
        BotDetectionMetrics? metrics = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _metrics = metrics;

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new ArgumentException("ConnectionString is required", nameof(options));
        }

        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = 10000 // Max cached entries
        });

        _flushTimer = new Timer(async _ => await FlushPendingWritesAsync(), null, _flushInterval, _flushInterval);
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            const string createTableSql = @"
                CREATE TABLE IF NOT EXISTS learned_weights (
                    id SERIAL PRIMARY KEY,
                    signature_type TEXT NOT NULL,
                    signature TEXT NOT NULL,
                    weight DOUBLE PRECISION NOT NULL DEFAULT 0.0,
                    confidence DOUBLE PRECISION NOT NULL DEFAULT 0.0,
                    observation_count INTEGER NOT NULL DEFAULT 1,
                    bot_count INTEGER NOT NULL DEFAULT 0,
                    human_count INTEGER NOT NULL DEFAULT 0,
                    first_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    last_seen TIMESTAMPTZ NOT NULL DEFAULT NOW(),
                    UNIQUE(signature_type, signature)
                );

                CREATE INDEX IF NOT EXISTS idx_learned_weights_type ON learned_weights(signature_type);
                CREATE INDEX IF NOT EXISTS idx_learned_weights_type_sig ON learned_weights(signature_type, signature);
                CREATE INDEX IF NOT EXISTS idx_learned_weights_confidence ON learned_weights(confidence DESC);
                CREATE INDEX IF NOT EXISTS idx_learned_weights_last_seen ON learned_weights(last_seen DESC);
            ";

            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.ExecuteAsync(createTableSql, commandTimeout: _options.CommandTimeoutSeconds);

            _initialized = true;
            _logger.LogInformation("PostgreSQL weight store initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<double> GetWeightAsync(string signatureType, string signature, CancellationToken ct = default)
    {
        var cacheKey = $"{signatureType}:{signature}";
        if (_cache.TryGetValue(cacheKey, out double cachedWeight))
        {
            return cachedWeight;
        }

        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT weight FROM learned_weights
            WHERE signature_type = @SignatureType AND signature = @Signature
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var weight = await connection.QueryFirstOrDefaultAsync<double?>(sql,
            new { SignatureType = signatureType, Signature = signature },
            commandTimeout: _options.CommandTimeoutSeconds);

        var result = weight ?? 0.0;
        _cache.Set(cacheKey, result, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(5) });

        return result;
    }

    public async Task<IReadOnlyDictionary<string, double>> GetWeightsAsync(
        string signatureType,
        IEnumerable<string> signatures,
        CancellationToken ct = default)
    {
        var sigList = signatures.ToList();
        var result = new Dictionary<string, double>();
        var toFetch = new List<string>();

        foreach (var sig in sigList)
        {
            var cacheKey = $"{signatureType}:{sig}";
            if (_cache.TryGetValue(cacheKey, out double cachedWeight))
            {
                result[sig] = cachedWeight;
            }
            else
            {
                toFetch.Add(sig);
            }
        }

        if (toFetch.Count == 0) return result;

        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT signature, weight FROM learned_weights
            WHERE signature_type = @SignatureType AND signature = ANY(@Signatures)
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var rows = await connection.QueryAsync<(string Signature, double Weight)>(sql,
            new { SignatureType = signatureType, Signatures = toFetch.ToArray() },
            commandTimeout: _options.CommandTimeoutSeconds);

        foreach (var row in rows)
        {
            result[row.Signature] = row.Weight;
            var cacheKey = $"{signatureType}:{row.Signature}";
            _cache.Set(cacheKey, row.Weight, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(5) });
        }

        // Set 0.0 for any signatures not found
        foreach (var sig in toFetch.Where(s => !result.ContainsKey(s)))
        {
            result[sig] = 0.0;
        }

        return result;
    }

    public async Task UpdateWeightAsync(
        string signatureType,
        string signature,
        double weight,
        double confidence,
        int observationCount,
        CancellationToken ct = default)
    {
        // Update cache immediately
        var cacheKey = $"{signatureType}:{signature}";
        _cache.Set(cacheKey, weight, new MemoryCacheEntryOptions { Size = 1, SlidingExpiration = TimeSpan.FromMinutes(5) });

        // Queue for background write
        _pendingWrites.Enqueue(new PendingWrite
        {
            SignatureType = signatureType,
            Signature = signature,
            Weight = weight,
            Confidence = confidence,
            ObservationCount = observationCount
        });
    }

    public async Task RecordObservationAsync(
        string signatureType,
        string signature,
        bool wasBot,
        double detectionConfidence,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // Use upsert with learning formula
        const string sql = @"
            INSERT INTO learned_weights (signature_type, signature, weight, confidence, observation_count, bot_count, human_count, first_seen, last_seen)
            VALUES (@SignatureType, @Signature, @InitialWeight, @Confidence, 1, @BotInc, @HumanInc, NOW(), NOW())
            ON CONFLICT (signature_type, signature) DO UPDATE SET
                bot_count = learned_weights.bot_count + @BotInc,
                human_count = learned_weights.human_count + @HumanInc,
                observation_count = learned_weights.observation_count + 1,
                weight = CASE
                    WHEN (learned_weights.bot_count + @BotInc + learned_weights.human_count + @HumanInc) > 0
                    THEN ((learned_weights.bot_count + @BotInc)::DOUBLE PRECISION / (learned_weights.bot_count + @BotInc + learned_weights.human_count + @HumanInc) - 0.5) * 2.0
                    ELSE 0.0
                END,
                confidence = LEAST(1.0, (learned_weights.observation_count + 1) / 100.0),
                last_seen = NOW()
        ";

        var initialWeight = wasBot ? 0.5 : -0.5;
        var botInc = wasBot ? 1 : 0;
        var humanInc = wasBot ? 0 : 1;

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.ExecuteAsync(sql, new
            {
                SignatureType = signatureType,
                Signature = signature,
                InitialWeight = initialWeight,
                Confidence = detectionConfidence,
                BotInc = botInc,
                HumanInc = humanInc
            }, commandTimeout: _options.CommandTimeoutSeconds);

            // Invalidate cache
            var cacheKey = $"{signatureType}:{signature}";
            _cache.Remove(cacheKey);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to record observation for {Type}:{Signature}", signatureType, signature);
        }
    }

    public async Task<IReadOnlyList<LearnedWeight>> GetAllWeightsAsync(string signatureType, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT signature_type as SignatureType, signature as Signature, weight as Weight,
                   confidence as Confidence, observation_count as ObservationCount,
                   first_seen as FirstSeen, last_seen as LastSeen
            FROM learned_weights
            WHERE signature_type = @SignatureType
            ORDER BY ABS(weight) DESC
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var results = await connection.QueryAsync<LearnedWeight>(sql,
            new { SignatureType = signatureType },
            commandTimeout: _options.CommandTimeoutSeconds);

        return results.ToList();
    }

    public async Task<WeightStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            SELECT
                COUNT(*) as TotalWeights,
                COUNT(*) FILTER (WHERE signature_type = 'ua_pattern') as UaPatternWeights,
                COUNT(*) FILTER (WHERE signature_type = 'ip_range') as IpRangeWeights,
                COUNT(*) FILTER (WHERE signature_type = 'path_pattern') as PathPatternWeights,
                COUNT(*) FILTER (WHERE signature_type = 'behavior_hash') as BehaviorHashWeights,
                COUNT(*) FILTER (WHERE signature_type = 'combined') as CombinedSignatureWeights,
                COALESCE(AVG(confidence), 0.0) as AverageConfidence,
                COUNT(*) FILTER (WHERE confidence >= 0.7) as HighConfidenceWeights,
                MIN(first_seen) as OldestWeight,
                MAX(last_seen) as NewestWeight
            FROM learned_weights
        ";

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var result = await connection.QueryFirstAsync<WeightStoreStats>(sql, commandTimeout: _options.CommandTimeoutSeconds);

        return result;
    }

    public async Task DecayOldWeightsAsync(TimeSpan maxAge, double decayFactor, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        const string sql = @"
            UPDATE learned_weights
            SET weight = weight * @DecayFactor,
                confidence = confidence * @DecayFactor
            WHERE last_seen < @CutoffDate
        ";

        var cutoffDate = DateTimeOffset.UtcNow - maxAge;

        await using var connection = new NpgsqlConnection(_options.ConnectionString);
        var updated = await connection.ExecuteAsync(sql,
            new { DecayFactor = decayFactor, CutoffDate = cutoffDate },
            commandTimeout: _options.CommandTimeoutSeconds);

        if (updated > 0)
        {
            _logger.LogInformation("Decayed {Count} old weights by factor {Factor}", updated, decayFactor);
        }
    }

    private async Task FlushPendingWritesAsync()
    {
        if (_pendingWrites.IsEmpty) return;

        if (!await _flushLock.WaitAsync(0)) return; // Skip if already flushing

        try
        {
            var writes = new List<PendingWrite>();
            while (_pendingWrites.TryDequeue(out var write) && writes.Count < 100)
            {
                writes.Add(write);
            }

            if (writes.Count == 0) return;

            await EnsureInitializedAsync();

            const string sql = @"
                INSERT INTO learned_weights (signature_type, signature, weight, confidence, observation_count, first_seen, last_seen)
                VALUES (@SignatureType, @Signature, @Weight, @Confidence, @ObservationCount, NOW(), NOW())
                ON CONFLICT (signature_type, signature) DO UPDATE SET
                    weight = @Weight,
                    confidence = @Confidence,
                    observation_count = @ObservationCount,
                    last_seen = NOW()
            ";

            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.ExecuteAsync(sql, writes, commandTimeout: _options.CommandTimeoutSeconds);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush pending weight writes");
        }
        finally
        {
            _flushLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _flushTimer.Dispose();
        _cache.Dispose();
        _initLock.Dispose();
        _flushLock.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;

        // Flush any remaining writes
        await FlushPendingWritesAsync();

        Dispose();
    }

    private class PendingWrite
    {
        public string SignatureType { get; init; } = string.Empty;
        public string Signature { get; init; } = string.Empty;
        public double Weight { get; init; }
        public double Confidence { get; init; }
        public int ObservationCount { get; init; }
    }
}
