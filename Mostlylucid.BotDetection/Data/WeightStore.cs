using System.Collections.Concurrent;
using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Store for learned weights that feed back to static analyzers.
///     The learning system updates these weights, and detectors read them to adjust confidence.
/// </summary>
/// <remarks>
///     <para>
///         Key concept: Each request gets a "signature" (by path, policy, UA pattern, etc.).
///         The learning system observes patterns and updates weights for signatures.
///         Static analyzers then use these weights to boost/reduce their confidence.
///     </para>
///     <para>
///         Weight types:
///         <list type="bullet">
///             <item>UA patterns: Weight adjustments for specific User-Agent patterns</item>
///             <item>IP ranges: Weight adjustments for IP address ranges</item>
///             <item>Path patterns: Weight adjustments for request path patterns</item>
///             <item>Behavior hashes: Weight adjustments for behavioral signatures</item>
///             <item>Combined signatures: Multi-factor signature weights</item>
///         </list>
///     </para>
/// </remarks>
public interface IWeightStore
{
    /// <summary>
    ///     Get the learned weight adjustment for a signature.
    ///     Returns 0.0 if no learned weight exists.
    /// </summary>
    Task<double> GetWeightAsync(string signatureType, string signature, CancellationToken ct = default);

    /// <summary>
    ///     Get multiple weights at once (batch lookup for efficiency).
    /// </summary>
    Task<IReadOnlyDictionary<string, double>> GetWeightsAsync(
        string signatureType,
        IEnumerable<string> signatures,
        CancellationToken ct = default);

    /// <summary>
    ///     Update a learned weight. Called by the learning system in the slow path.
    /// </summary>
    Task UpdateWeightAsync(
        string signatureType,
        string signature,
        double weight,
        double confidence,
        int observationCount,
        CancellationToken ct = default);

    /// <summary>
    ///     Increment observation count and optionally adjust weight.
    ///     Used when we see a signature again and want to reinforce/decay the weight.
    /// </summary>
    Task RecordObservationAsync(
        string signatureType,
        string signature,
        bool wasBot,
        double detectionConfidence,
        CancellationToken ct = default);

    /// <summary>
    ///     Get all weights for a signature type (for bulk loading into memory cache).
    /// </summary>
    Task<IReadOnlyList<LearnedWeight>> GetAllWeightsAsync(
        string signatureType,
        CancellationToken ct = default);

    /// <summary>
    ///     Get statistics about the weight store.
    /// </summary>
    Task<WeightStoreStats> GetStatsAsync(CancellationToken ct = default);

    /// <summary>
    ///     Decay old weights that haven't been seen recently.
    /// </summary>
    Task DecayOldWeightsAsync(TimeSpan maxAge, double decayFactor, CancellationToken ct = default);
}

/// <summary>
///     A learned weight for a signature.
/// </summary>
public record LearnedWeight
{
    public required string SignatureType { get; init; }
    public required string Signature { get; init; }

    /// <summary>
    ///     The weight adjustment (-1.0 to +1.0).
    ///     Positive = more likely bot, negative = more likely human.
    /// </summary>
    public required double Weight { get; init; }

    /// <summary>
    ///     Confidence in this weight (0.0 to 1.0).
    ///     Based on observation count and consistency.
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    ///     Number of times this signature was observed.
    /// </summary>
    public required int ObservationCount { get; init; }

    /// <summary>
    ///     When this weight was first created.
    /// </summary>
    public required DateTimeOffset FirstSeen { get; init; }

    /// <summary>
    ///     When this weight was last updated.
    /// </summary>
    public required DateTimeOffset LastSeen { get; init; }
}

/// <summary>
///     Statistics about the weight store.
/// </summary>
public record WeightStoreStats
{
    public int TotalWeights { get; init; }
    public int UaPatternWeights { get; init; }
    public int IpRangeWeights { get; init; }
    public int PathPatternWeights { get; init; }
    public int BehaviorHashWeights { get; init; }
    public int CombinedSignatureWeights { get; init; }
    public double AverageConfidence { get; init; }
    public int HighConfidenceWeights { get; init; }
    public DateTimeOffset? OldestWeight { get; init; }
    public DateTimeOffset? NewestWeight { get; init; }
}

/// <summary>
///     Well-known signature types for the weight store.
/// </summary>
public static class SignatureTypes
{
    public const string UaPattern = "ua_pattern";
    public const string IpRange = "ip_range";
    public const string PathPattern = "path_pattern";
    public const string BehaviorHash = "behavior_hash";
    public const string CombinedSignature = "combined";
    public const string DetectorName = "detector";
    public const string HeaderPattern = "header_pattern";

    /// <summary>
    ///     Heuristic detector feature weights.
    ///     Used by <see cref="Detectors.HeuristicDetector" /> for learned classification.
    /// </summary>
    public const string HeuristicFeature = "heuristic_feature";
}

/// <summary>
///     SQLite implementation of the weight store with sliding expiration memory cache.
///     Uses a CQRS-style pattern with write-behind:
///     - Reads: Hit memory cache first (fast path), fall back to SQLite on miss
///     - Writes: Update cache immediately, queue SQLite writes for background flush
///     Sliding expiration provides automatic LRU-like eviction behavior.
/// </summary>
public class SqliteWeightStore : IWeightStore, IAsyncDisposable, IDisposable
{
    private const string TableName = "learned_weights";

    // Memory cache with sliding expiration - auto-evicts least recently used entries
    private readonly MemoryCache _cache;
    private readonly int _cacheSize;
    private readonly string _connectionString;
    private readonly TimeSpan _flushInterval = TimeSpan.FromMilliseconds(500);
    private readonly SemaphoreSlim _flushLock = new(1, 1);
    private readonly Timer _flushTimer;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly ILogger<SqliteWeightStore> _logger;
    private readonly BotDetectionMetrics? _metrics;

    // Write-behind queue for batched SQLite persistence
    // Cache holds current state, SQLite is persisted async every 500ms
    private readonly ConcurrentDictionary<string, PendingWrite> _pendingWrites = new();
    private readonly TimeSpan _slidingExpiration;
    private bool _disposed;

    private bool _initialized;

    public SqliteWeightStore(
        ILogger<SqliteWeightStore> logger,
        IOptions<BotDetectionOptions> options,
        BotDetectionMetrics? metrics = null)
    {
        _logger = logger;
        _metrics = metrics;
        _cacheSize = options.Value.WeightStoreCacheSize > 0
            ? options.Value.WeightStoreCacheSize
            : 1000;

        // Sliding expiration provides automatic LRU-like behavior
        // Entries not accessed within this window are evicted
        _slidingExpiration = TimeSpan.FromMinutes(30);

        // Configure MemoryCache with size limit for bounded memory usage
        _cache = new MemoryCache(new MemoryCacheOptions
        {
            SizeLimit = _cacheSize,
            CompactionPercentage = 0.25 // Remove 25% when limit reached
        });

        var dbPath = options.Value.DatabasePath
                     ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");

        _connectionString = $"Data Source={dbPath}";

        // Start background flush timer for write-behind pattern
        _flushTimer = new Timer(FlushPendingWritesCallback, null, _flushInterval, _flushInterval);

        _logger.LogDebug(
            "WeightStore initialized with sliding cache (size={CacheSize}, expiration={Expiration}min, flush={Flush}s)",
            _cacheSize, _slidingExpiration.TotalMinutes, _flushInterval.TotalSeconds);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // Stop the timer
        await _flushTimer.DisposeAsync();

        // Flush remaining writes
        try
        {
            await FlushPendingWritesAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush pending writes during async dispose");
        }

        _cache.Dispose();
        _flushLock.Dispose();
        _initLock.Dispose();

        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public async Task<double> GetWeightAsync(string signatureType, string signature, CancellationToken ct = default)
    {
        var key = CacheKey(signatureType, signature);

        // Check cache first (fast path - no DB access)
        if (_cache.TryGetValue(key, out LearnedWeight? cached) && cached != null)
        {
            _metrics?.RecordCacheHit(signatureType);
            return cached.Weight * cached.Confidence; // Weighted by confidence
        }

        _metrics?.RecordCacheMiss(signatureType);

        // Cache miss - load from DB
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            SELECT weight, confidence, observation_count, first_seen, last_seen FROM {TableName}
            WHERE signature_type = @type AND signature = @sig
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@type", signatureType);
        cmd.Parameters.AddWithValue("@sig", signature);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var weight = reader.GetDouble(0);
            var confidence = reader.GetDouble(1);

            // Cache the result for future reads (sliding expiration auto-evicts stale entries)
            var learnedWeight = new LearnedWeight
            {
                SignatureType = signatureType,
                Signature = signature,
                Weight = weight,
                Confidence = confidence,
                ObservationCount = reader.GetInt32(2),
                FirstSeen = DateTimeOffset.Parse(reader.GetString(3)),
                LastSeen = DateTimeOffset.Parse(reader.GetString(4))
            };
            _cache.Set(key, learnedWeight, GetCacheEntryOptions());

            return weight * confidence;
        }

        return 0.0;
    }

    public async Task<IReadOnlyDictionary<string, double>> GetWeightsAsync(
        string signatureType,
        IEnumerable<string> signatures,
        CancellationToken ct = default)
    {
        var result = new Dictionary<string, double>();
        var sigList = signatures.ToList();

        // Check cache for all (fast path)
        foreach (var sig in sigList)
        {
            var key = CacheKey(signatureType, sig);
            if (_cache.TryGetValue(key, out LearnedWeight? cached) && cached != null)
            {
                result[sig] = cached.Weight * cached.Confidence;
                _metrics?.RecordCacheHit(signatureType);
            }
        }

        if (result.Count == sigList.Count)
            return result; // All found in cache

        // Fetch missing from DB
        var missing = sigList.Where(s => !result.ContainsKey(s)).ToList();
        if (missing.Count == 0)
            return result;

        // Record cache misses for missing items
        for (var i = 0; i < missing.Count; i++)
            _metrics?.RecordCacheMiss(signatureType);

        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Build parameterized query for missing signatures
        var placeholders = string.Join(",", missing.Select((_, i) => $"@sig{i}"));
        var sql = $@"
            SELECT signature, weight, confidence, observation_count, first_seen, last_seen FROM {TableName}
            WHERE signature_type = @type AND signature IN ({placeholders})
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@type", signatureType);
        for (var i = 0; i < missing.Count; i++) cmd.Parameters.AddWithValue($"@sig{i}", missing[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var cacheOptions = GetCacheEntryOptions();
        while (await reader.ReadAsync(ct))
        {
            var sig = reader.GetString(0);
            var weight = reader.GetDouble(1);
            var confidence = reader.GetDouble(2);

            // Cache the result
            var learnedWeight = new LearnedWeight
            {
                SignatureType = signatureType,
                Signature = sig,
                Weight = weight,
                Confidence = confidence,
                ObservationCount = reader.GetInt32(3),
                FirstSeen = DateTimeOffset.Parse(reader.GetString(4)),
                LastSeen = DateTimeOffset.Parse(reader.GetString(5))
            };
            _cache.Set(CacheKey(signatureType, sig), learnedWeight, cacheOptions);

            result[sig] = weight * confidence;
        }

        return result;
    }

    public Task UpdateWeightAsync(
        string signatureType,
        string signature,
        double weight,
        double confidence,
        int observationCount,
        CancellationToken ct = default)
    {
        var key = CacheKey(signatureType, signature);

        // Update cache immediately (source of truth for reads)
        var learnedWeight = new LearnedWeight
        {
            SignatureType = signatureType,
            Signature = signature,
            Weight = weight,
            Confidence = confidence,
            ObservationCount = observationCount,
            FirstSeen = DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow
        };
        _cache.Set(key, learnedWeight, GetCacheEntryOptions());

        // Queue for async SQLite persistence (write-behind)
        QueueWrite(signatureType, signature, weight, confidence, observationCount);

        _logger.LogDebug(
            "Updated weight: {Type}/{Signature} = {Weight:F3} (conf={Confidence:F2}, count={Count})",
            signatureType, signature, weight, confidence, observationCount);

        return Task.CompletedTask;
    }

    public Task RecordObservationAsync(
        string signatureType,
        string signature,
        bool wasBot,
        double detectionConfidence,
        CancellationToken ct = default)
    {
        var key = CacheKey(signatureType, signature);

        // Calculate new weight using EMA in memory
        var alpha = 0.1; // Learning rate
        var weightDelta = wasBot ? detectionConfidence : -detectionConfidence;

        double newWeight;
        double newConfidence;
        int newObservationCount;

        // Get existing cached value if present
        if (_cache.TryGetValue(key, out LearnedWeight? existing) && existing != null)
        {
            // Apply EMA: new_weight = old_weight * (1-α) + delta * α
            newWeight = existing.Weight * (1 - alpha) + weightDelta * alpha;
            newConfidence = Math.Min(1.0, existing.Confidence + detectionConfidence * 0.01);
            newObservationCount = existing.ObservationCount + 1;
        }
        else
        {
            // First observation
            newWeight = weightDelta;
            newConfidence = detectionConfidence;
            newObservationCount = 1;
        }

        // Update cache immediately (source of truth for reads)
        var learnedWeight = new LearnedWeight
        {
            SignatureType = signatureType,
            Signature = signature,
            Weight = newWeight,
            Confidence = newConfidence,
            ObservationCount = newObservationCount,
            FirstSeen = existing?.FirstSeen ?? DateTimeOffset.UtcNow,
            LastSeen = DateTimeOffset.UtcNow
        };
        _cache.Set(key, learnedWeight, GetCacheEntryOptions());

        // Queue for async SQLite persistence (write-behind)
        // For observations, we store the final computed values since cache is source of truth
        QueueWrite(signatureType, signature, newWeight, newConfidence, newObservationCount);

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<LearnedWeight>> GetAllWeightsAsync(
        string signatureType,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            SELECT signature_type, signature, weight, confidence, observation_count, first_seen, last_seen
            FROM {TableName}
            WHERE signature_type = @type
            ORDER BY confidence DESC, observation_count DESC
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@type", signatureType);

        var results = new List<LearnedWeight>();
        var cacheOptions = GetCacheEntryOptions();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var learnedWeight = new LearnedWeight
            {
                SignatureType = reader.GetString(0),
                Signature = reader.GetString(1),
                Weight = reader.GetDouble(2),
                Confidence = reader.GetDouble(3),
                ObservationCount = reader.GetInt32(4),
                FirstSeen = DateTimeOffset.Parse(reader.GetString(5)),
                LastSeen = DateTimeOffset.Parse(reader.GetString(6))
            };
            results.Add(learnedWeight);

            // Warm up cache with loaded weights
            _cache.Set(CacheKey(signatureType, learnedWeight.Signature), learnedWeight, cacheOptions);
        }

        return results;
    }

    public async Task<WeightStoreStats> GetStatsAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sql = $@"
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN signature_type = '{SignatureTypes.UaPattern}' THEN 1 ELSE 0 END) as ua_count,
                SUM(CASE WHEN signature_type = '{SignatureTypes.IpRange}' THEN 1 ELSE 0 END) as ip_count,
                SUM(CASE WHEN signature_type = '{SignatureTypes.PathPattern}' THEN 1 ELSE 0 END) as path_count,
                SUM(CASE WHEN signature_type = '{SignatureTypes.BehaviorHash}' THEN 1 ELSE 0 END) as behavior_count,
                SUM(CASE WHEN signature_type = '{SignatureTypes.CombinedSignature}' THEN 1 ELSE 0 END) as combined_count,
                AVG(confidence) as avg_confidence,
                SUM(CASE WHEN confidence > 0.7 THEN 1 ELSE 0 END) as high_confidence,
                MIN(first_seen) as oldest,
                MAX(last_seen) as newest
            FROM {TableName}
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        if (await reader.ReadAsync(ct))
            return new WeightStoreStats
            {
                TotalWeights = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                UaPatternWeights = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                IpRangeWeights = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                PathPatternWeights = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                BehaviorHashWeights = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
                CombinedSignatureWeights = reader.IsDBNull(5) ? 0 : reader.GetInt32(5),
                AverageConfidence = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                HighConfidenceWeights = reader.IsDBNull(7) ? 0 : reader.GetInt32(7),
                OldestWeight = reader.IsDBNull(8) ? null : DateTimeOffset.Parse(reader.GetString(8)),
                NewestWeight = reader.IsDBNull(9) ? null : DateTimeOffset.Parse(reader.GetString(9))
            };

        return new WeightStoreStats();
    }

    public async Task DecayOldWeightsAsync(TimeSpan maxAge, double decayFactor, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var cutoff = DateTimeOffset.UtcNow.Subtract(maxAge).ToString("O");

        // Decay old weights
        var sql = $@"
            UPDATE {TableName}
            SET weight = weight * @decay,
                confidence = confidence * @decay
            WHERE last_seen < @cutoff
        ";

        await using var cmd = new SqliteCommand(sql, conn);
        cmd.Parameters.AddWithValue("@decay", decayFactor);
        cmd.Parameters.AddWithValue("@cutoff", cutoff);

        var updated = await cmd.ExecuteNonQueryAsync(ct);

        // Delete weights that have decayed below threshold
        var deleteSql = $@"
            DELETE FROM {TableName}
            WHERE confidence < 0.01 OR (ABS(weight) < 0.01 AND observation_count < 5)
        ";

        await using var deleteCmd = new SqliteCommand(deleteSql, conn);
        var deleted = await deleteCmd.ExecuteNonQueryAsync(ct);

        if (updated > 0 || deleted > 0)
        {
            _logger.LogInformation(
                "Weight decay: {Updated} decayed, {Deleted} deleted",
                updated, deleted);

            // Compact cache to remove stale entries (MemoryCache handles this automatically via sliding expiration)
            _cache.Compact(0.25);
        }
    }

    /// <summary>
    ///     Creates a cache key for a signature type and signature.
    /// </summary>
    private static string CacheKey(string signatureType, string signature)
    {
        return $"{signatureType}:{signature}";
    }

    /// <summary>
    ///     Creates a tag key for a signature type (for bulk eviction).
    /// </summary>
    private static string TagKey(string signatureType)
    {
        return $"tag:{signatureType}";
    }

    /// <summary>
    ///     Gets cache entry options with sliding expiration and size.
    /// </summary>
    private MemoryCacheEntryOptions GetCacheEntryOptions()
    {
        return new MemoryCacheEntryOptions()
            .SetSlidingExpiration(_slidingExpiration)
            .SetSize(1); // Each entry counts as 1 toward size limit
    }

    /// <summary>
    ///     Timer callback for flushing pending writes to SQLite.
    /// </summary>
    private void FlushPendingWritesCallback(object? state)
    {
        // Fire and forget - errors are logged but don't crash the timer
        _ = FlushPendingWritesAsync(CancellationToken.None);
    }

    /// <summary>
    ///     Flushes all pending writes to SQLite in a single batch.
    ///     Called periodically by the timer and on dispose.
    /// </summary>
    public async Task FlushPendingWritesAsync(CancellationToken ct = default)
    {
        if (_pendingWrites.IsEmpty) return;

        // Only one flush at a time
        if (!await _flushLock.WaitAsync(0, ct)) return;

        var sw = Stopwatch.StartNew();
        var batchSize = 0;
        var success = false;

        try
        {
            await EnsureInitializedAsync(ct);

            // Snapshot and clear pending writes atomically
            var writes = new List<PendingWrite>();
            foreach (var key in _pendingWrites.Keys.ToList())
                if (_pendingWrites.TryRemove(key, out var write))
                    writes.Add(write);

            if (writes.Count == 0) return;
            batchSize = writes.Count;

            _metrics?.UpdatePendingWrites(_pendingWrites.Count);

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // Use transaction for batch write
            await using var transaction = await conn.BeginTransactionAsync(ct);

            try
            {
                // Single SQL statement for all writes (cache holds final computed values)
                var sql = $@"
                    INSERT INTO {TableName} (signature_type, signature, weight, confidence, observation_count, first_seen, last_seen)
                    VALUES (@type, @sig, @weight, @conf, @count, @now, @now)
                    ON CONFLICT(signature_type, signature) DO UPDATE SET
                        weight = @weight,
                        confidence = @conf,
                        observation_count = @count,
                        last_seen = @now
                ";

                foreach (var write in writes)
                {
                    var now = write.Timestamp.ToString("O");

                    await using var cmd = new SqliteCommand(sql, conn, (SqliteTransaction)transaction);
                    cmd.Parameters.AddWithValue("@type", write.SignatureType);
                    cmd.Parameters.AddWithValue("@sig", write.Signature);
                    cmd.Parameters.AddWithValue("@weight", write.Weight);
                    cmd.Parameters.AddWithValue("@conf", write.Confidence);
                    cmd.Parameters.AddWithValue("@count", write.ObservationCount);
                    cmd.Parameters.AddWithValue("@now", now);
                    await cmd.ExecuteNonQueryAsync(ct);
                }

                await transaction.CommitAsync(ct);
                success = true;
                _logger.LogDebug("Flushed {Count} pending writes to SQLite in {Duration:F1}ms", writes.Count,
                    sw.ElapsedMilliseconds);
            }
            catch
            {
                await transaction.RollbackAsync(ct);
                throw;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to flush pending writes to SQLite");
        }
        finally
        {
            sw.Stop();
            _metrics?.RecordFlush(batchSize, sw.Elapsed, success);
            _flushLock.Release();
        }
    }

    /// <summary>
    ///     Queues a write operation for background persistence.
    ///     Latest write wins - if multiple updates happen before flush, only the final value persists.
    /// </summary>
    private void QueueWrite(string signatureType, string signature, double weight, double confidence,
        int observationCount)
    {
        var key = CacheKey(signatureType, signature);
        _pendingWrites[key] = new PendingWrite
        {
            SignatureType = signatureType,
            Signature = signature,
            Weight = weight,
            Confidence = confidence,
            ObservationCount = observationCount,
            Timestamp = DateTimeOffset.UtcNow
        };

        _metrics?.RecordCacheWrite(signatureType);
        _metrics?.UpdatePendingWrites(_pendingWrites.Count);
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

            var sql = $@"
                CREATE TABLE IF NOT EXISTS {TableName} (
                    signature_type TEXT NOT NULL,
                    signature TEXT NOT NULL,
                    weight REAL NOT NULL,
                    confidence REAL NOT NULL,
                    observation_count INTEGER NOT NULL DEFAULT 1,
                    first_seen TEXT NOT NULL,
                    last_seen TEXT NOT NULL,
                    PRIMARY KEY (signature_type, signature)
                );

                CREATE INDEX IF NOT EXISTS idx_signature_type ON {TableName}(signature_type);
                CREATE INDEX IF NOT EXISTS idx_confidence ON {TableName}(confidence);
                CREATE INDEX IF NOT EXISTS idx_last_seen ON {TableName}(last_seen);
            ";

            await using var cmd = new SqliteCommand(sql, conn);
            await cmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
            _logger.LogDebug("Weight store initialized");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Evicts all cached entries for a specific signature type (tag-based eviction).
    /// </summary>
    public void EvictByTag(string signatureType)
    {
        // MemoryCache doesn't natively support tag-based eviction, but we can compact
        // For now, just compact - sliding expiration will handle stale entries
        _cache.Compact(0.1);
        _logger.LogDebug("Compacted cache for signature type: {SignatureType}", signatureType);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true;

        if (disposing)
        {
            // Stop the timer first
            _flushTimer.Dispose();

            // Flush any remaining pending writes synchronously
            try
            {
                FlushPendingWritesAsync(CancellationToken.None).GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to flush pending writes during dispose");
            }

            _cache.Dispose();
            _flushLock.Dispose();
            _initLock.Dispose();
        }
    }

    /// <summary>
    ///     Represents a pending write operation to be flushed to SQLite.
    ///     Cache holds the current computed values; this just persists them.
    /// </summary>
    private record PendingWrite
    {
        public required string SignatureType { get; init; }
        public required string Signature { get; init; }
        public required double Weight { get; init; }
        public required double Confidence { get; init; }
        public required int ObservationCount { get; init; }
        public required DateTimeOffset Timestamp { get; init; }
    }
}