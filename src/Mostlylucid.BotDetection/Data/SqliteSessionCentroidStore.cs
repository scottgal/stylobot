using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Aggregate value held per signature in the session centroid hot tier.
///     All optional vector fields are preserved across merges (last write wins).
/// </summary>
public sealed record SessionCentroidEntry(
    string SignatureId,
    float[] Vector,
    float[]? VelocityVector,
    float[]? VarianceVector,
    float[]? FreqFingerprint,
    string? ClusterId,
    int CompressionLevel,
    bool IsBot,
    double BotProbability,
    double Priority,
    long UpdatedAtTicks);

/// <summary>Discrete write op fed into the <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/> drainer.</summary>
public sealed record SessionCentroidWriteOp(SessionCentroidRow Row);

/// <summary>
///     SQLite-backed <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/> for
///     session centroids. Eviction priority is <c>DecisionNecessity.ColdnessScore</c>
///     keyed on <c>BotProbability</c> (uncertainty near threshold) and threat
///     (IsBot x BotProbability) so high-risk sessions survive memory pressure.
/// </summary>
public sealed class SqliteSessionCentroidStore
    : WriteBehindLfuStore<string, SessionCentroidEntry, SessionCentroidWriteOp>,
      ISessionCentroidStore,
      IStoreInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionCentroidStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _initTask;

    private const double HalfLifeSeconds = 604_800.0; // 7 days
    private const double Threshold        = 0.70;

    // ctor for DI (primary path; derives own .db file from DatabasePath).
    public SqliteSessionCentroidStore(
        IOptions<BotDetectionOptions> options,
        ILogger<SqliteSessionCentroidStore> logger)
        : base(
            maxEntries: options.Value.SelfMaintenance.SessionCacheSize,
            writeQueueCapacity: options.Value.SelfMaintenance.SessionCacheSize * 2,
            batchMaxSize: 256,
            drainInterval: TimeSpan.FromMilliseconds(500),
            logger: logger,
            keyComparer: StringComparer.Ordinal)
    {
        _logger = logger;
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        StoreDbDirectory.EnsureExists(basePath);
        _connectionString = $"Data Source={Path.Combine(basePath, "session_centroids.db")};Cache=Shared";
    }

    // ctor for test backward compat (accepts explicit connection string; uses default caps).
    public SqliteSessionCentroidStore(
        string connectionString,
        ILogger<SqliteSessionCentroidStore> logger)
        : base(
            maxEntries: 2_000,
            writeQueueCapacity: 4_000,
            batchMaxSize: 256,
            drainInterval: TimeSpan.FromMilliseconds(500),
            logger: logger,
            keyComparer: StringComparer.Ordinal)
    {
        _logger = logger;
        _connectionString = connectionString;
    }

    // ── IStoreInitializer ──────────────────────────────────────────────────

    public Task InitializeAsync(CancellationToken ct = default) =>
        _initTask ??= InitializeOnceAsync(ct);

    private async Task InitializeOnceAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS session_centroids (
                signature_id      TEXT PRIMARY KEY,
                vector            BLOB    NOT NULL,
                velocity_vector   BLOB,
                variance_vector   BLOB,
                freq_fingerprint  BLOB,
                cluster_id        TEXT,
                compression_level INTEGER NOT NULL DEFAULT 0,
                is_bot            INTEGER NOT NULL DEFAULT 0,
                bot_probability   REAL    NOT NULL DEFAULT 0.0,
                priority          REAL    NOT NULL DEFAULT 0.5,
                updated_at        INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_sesc_updated ON session_centroids(updated_at);
            CREATE INDEX IF NOT EXISTS idx_sesc_cluster  ON session_centroids(cluster_id);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Hot-path facade (non-blocking; for Task B Slim* wiring) ───────────

    public void RecordSession(SessionCentroidRow row)
    {
        if (string.IsNullOrEmpty(row.SignatureId)) return;
        Record(row.SignatureId, new SessionCentroidWriteOp(row));
    }

    // ── ISessionCentroidStore ──────────────────────────────────────────────

    /// <summary>
    ///     Synchronous one-shot durable upsert (tests/admin). The hot path uses
    ///     <see cref="RecordSession"/> (write-behind); do not call this per-request.
    /// </summary>
    public async Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
    {
        try
        {
            await _writeLock.WaitAsync(ct);
            try
            {
                await using var conn = new SqliteConnection(_connectionString);
                await conn.OpenAsync(ct);
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO session_centroids
                        (signature_id, vector, velocity_vector, variance_vector, freq_fingerprint,
                         cluster_id, compression_level, is_bot, bot_probability, priority, updated_at)
                    VALUES (@sig,@vec,@vel,@var,@freq,@cid,@lvl,@bot,@prob,@pri,@ts)
                    ON CONFLICT(signature_id) DO UPDATE SET
                        vector=excluded.vector, velocity_vector=excluded.velocity_vector,
                        variance_vector=excluded.variance_vector, freq_fingerprint=excluded.freq_fingerprint,
                        cluster_id=excluded.cluster_id, compression_level=excluded.compression_level,
                        is_bot=excluded.is_bot, bot_probability=excluded.bot_probability,
                        priority=excluded.priority, updated_at=excluded.updated_at;
                    """;
                cmd.Parameters.AddWithValue("@sig",  row.SignatureId);
                cmd.Parameters.AddWithValue("@vec",  CentroidFloatPacker.Pack(row.Vector));
                cmd.Parameters.AddWithValue("@vel",  row.VelocityVector  != null ? (object)CentroidFloatPacker.Pack(row.VelocityVector)  : DBNull.Value);
                cmd.Parameters.AddWithValue("@var",  row.VarianceVector  != null ? (object)CentroidFloatPacker.Pack(row.VarianceVector)  : DBNull.Value);
                cmd.Parameters.AddWithValue("@freq", row.FreqFingerprint != null ? (object)CentroidFloatPacker.Pack(row.FreqFingerprint) : DBNull.Value);
                cmd.Parameters.AddWithValue("@cid",  (object?)row.ClusterId ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@lvl",  row.CompressionLevel);
                cmd.Parameters.AddWithValue("@bot",  row.IsBot ? 1 : 0);
                cmd.Parameters.AddWithValue("@prob", row.BotProbability);
                cmd.Parameters.AddWithValue("@pri",  row.Priority);
                cmd.Parameters.AddWithValue("@ts",   DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally { _writeLock.Release(); }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertSession direct write failed for {Sig}", row.SignatureId); }
    }

    public async Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(
        int limit, CancellationToken ct = default)
    {
        var result = new List<SessionCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, velocity_vector, variance_vector, freq_fingerprint,
                       cluster_id, compression_level, is_bot, bot_probability, priority
                FROM session_centroids ORDER BY updated_at DESC LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new SessionCentroidRow
                {
                    SignatureId      = reader.GetString(0),
                    Vector           = CentroidFloatPacker.Unpack((byte[])reader[1]),
                    VelocityVector   = reader.IsDBNull(2) ? null : CentroidFloatPacker.Unpack((byte[])reader[2]),
                    VarianceVector   = reader.IsDBNull(3) ? null : CentroidFloatPacker.Unpack((byte[])reader[3]),
                    FreqFingerprint  = reader.IsDBNull(4) ? null : CentroidFloatPacker.Unpack((byte[])reader[4]),
                    ClusterId        = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CompressionLevel = reader.GetInt32(6),
                    IsBot            = reader.GetInt32(7) != 0,
                    BotProbability   = reader.GetDouble(8),
                    Priority         = reader.GetDouble(9),
                });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentSessions failed"); }
        return result;
    }

    public async Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM session_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoffEpochSeconds);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} session centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneSessionCentroids failed"); }
    }

    // ── WriteBehindLfuStore hooks ──────────────────────────────────────────

    protected override SessionCentroidEntry CreateInitial(string key, SessionCentroidWriteOp op)
    {
        var r = op.Row;
        return new SessionCentroidEntry(
            r.SignatureId, r.Vector, r.VelocityVector, r.VarianceVector, r.FreqFingerprint,
            r.ClusterId, r.CompressionLevel, r.IsBot, r.BotProbability, r.Priority,
            UpdatedAtTicks: DateTimeOffset.UtcNow.UtcTicks);
    }

    protected override SessionCentroidEntry MergeIntoExisting(
        string key, SessionCentroidEntry existing, SessionCentroidWriteOp op)
    {
        var r = op.Row;
        return existing with
        {
            Vector           = r.Vector,
            VelocityVector   = r.VelocityVector,
            VarianceVector   = r.VarianceVector,
            FreqFingerprint  = r.FreqFingerprint,
            ClusterId        = r.ClusterId,
            CompressionLevel = r.CompressionLevel,
            IsBot            = r.IsBot,
            BotProbability   = r.BotProbability,
            Priority         = r.Priority,
            UpdatedAtTicks   = DateTimeOffset.UtcNow.UtcTicks
        };
    }

    protected override long ColdnessScore(SessionCentroidEntry entry)
    {
        var ageSeconds = (DateTimeOffset.UtcNow.UtcTicks - entry.UpdatedAtTicks)
                         / (double)TimeSpan.TicksPerSecond;
        return DecisionNecessity.ColdnessScore(
            botProbability: entry.BotProbability,
            threat:         entry.IsBot ? entry.BotProbability : 0.0,
            ageSeconds:     ageSeconds,
            threshold:      Threshold,
            halfLifeSeconds: HalfLifeSeconds);
    }

    /// <summary>Exposes the LFU coldness ranking for the hot entry at <paramref name="key"/>.</summary>
    public long GetColdnessScore(string key)
    {
        var entry = TryGetHot(key);
        return entry is null ? 0L : ColdnessScore(entry);
    }

    protected override async ValueTask<SessionCentroidEntry?> LoadFromDurableTierAsync(
        string key, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, velocity_vector, variance_vector, freq_fingerprint,
                       cluster_id, compression_level, is_bot, bot_probability, priority, updated_at
                FROM session_centroids WHERE signature_id = @sig
                """;
            cmd.Parameters.AddWithValue("@sig", key);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var updatedAt = reader.GetInt64(10);
            return new SessionCentroidEntry(
                reader.GetString(0),
                CentroidFloatPacker.Unpack((byte[])reader[1]),
                reader.IsDBNull(2) ? null : CentroidFloatPacker.Unpack((byte[])reader[2]),
                reader.IsDBNull(3) ? null : CentroidFloatPacker.Unpack((byte[])reader[3]),
                reader.IsDBNull(4) ? null : CentroidFloatPacker.Unpack((byte[])reader[4]),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetInt32(6),
                reader.GetInt32(7) != 0,
                reader.GetDouble(8),
                reader.GetDouble(9),
                UpdatedAtTicks: DateTimeOffset.FromUnixTimeSeconds(updatedAt).UtcTicks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LoadFromDurableTier failed for session {Key}", key);
            return null;
        }
    }

    protected override async Task PersistBatchAsync(
        IReadOnlyList<SessionCentroidWriteOp> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO session_centroids
                    (signature_id, vector, velocity_vector, variance_vector, freq_fingerprint,
                     cluster_id, compression_level, is_bot, bot_probability, priority, updated_at)
                VALUES ($sig,$vec,$vel,$var,$freq,$cid,$lvl,$bot,$prob,$pri,$ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, velocity_vector=excluded.velocity_vector,
                    variance_vector=excluded.variance_vector, freq_fingerprint=excluded.freq_fingerprint,
                    cluster_id=excluded.cluster_id, compression_level=excluded.compression_level,
                    is_bot=excluded.is_bot, bot_probability=excluded.bot_probability,
                    priority=excluded.priority, updated_at=excluded.updated_at
                """;
            var pSig  = cmd.CreateParameter(); pSig.ParameterName  = "$sig";  cmd.Parameters.Add(pSig);
            var pVec  = cmd.CreateParameter(); pVec.ParameterName  = "$vec";  cmd.Parameters.Add(pVec);
            var pVel  = cmd.CreateParameter(); pVel.ParameterName  = "$vel";  cmd.Parameters.Add(pVel);
            var pVar  = cmd.CreateParameter(); pVar.ParameterName  = "$var";  cmd.Parameters.Add(pVar);
            var pFreq = cmd.CreateParameter(); pFreq.ParameterName = "$freq"; cmd.Parameters.Add(pFreq);
            var pCid  = cmd.CreateParameter(); pCid.ParameterName  = "$cid";  cmd.Parameters.Add(pCid);
            var pLvl  = cmd.CreateParameter(); pLvl.ParameterName  = "$lvl";  cmd.Parameters.Add(pLvl);
            var pBot  = cmd.CreateParameter(); pBot.ParameterName  = "$bot";  cmd.Parameters.Add(pBot);
            var pProb = cmd.CreateParameter(); pProb.ParameterName = "$prob"; cmd.Parameters.Add(pProb);
            var pPri  = cmd.CreateParameter(); pPri.ParameterName  = "$pri";  cmd.Parameters.Add(pPri);
            var pTs   = cmd.CreateParameter(); pTs.ParameterName   = "$ts";   cmd.Parameters.Add(pTs);

            var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var op in batch)
            {
                var r = op.Row;
                pSig.Value  = r.SignatureId;
                pVec.Value  = CentroidFloatPacker.Pack(r.Vector);
                pVel.Value  = r.VelocityVector  != null ? (object)CentroidFloatPacker.Pack(r.VelocityVector)  : DBNull.Value;
                pVar.Value  = r.VarianceVector  != null ? (object)CentroidFloatPacker.Pack(r.VarianceVector)  : DBNull.Value;
                pFreq.Value = r.FreqFingerprint != null ? (object)CentroidFloatPacker.Pack(r.FreqFingerprint) : DBNull.Value;
                pCid.Value  = (object?)r.ClusterId ?? DBNull.Value;
                pLvl.Value  = r.CompressionLevel;
                pBot.Value  = r.IsBot ? 1 : 0;
                pProb.Value = r.BotProbability;
                pPri.Value  = r.Priority;
                pTs.Value   = nowEpoch;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }
}
