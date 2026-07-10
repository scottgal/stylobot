using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Aggregate value held per signature in the intent centroid hot tier.
///     <c>UpdatedAtTicks</c> drives the recency factor in <c>DecisionNecessity.ColdnessScore</c>.
/// </summary>
public sealed record IntentCentroidEntry(
    string SignatureId,
    float[] Vector,
    double ThreatScore,
    string IntentCategory,
    long UpdatedAtTicks);

/// <summary>Discrete write op fed into the <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/> drainer.</summary>
public sealed record IntentCentroidWriteOp(
    string SignatureId,
    float[] Vector,
    double ThreatScore,
    string IntentCategory);

/// <summary>
///     SQLite-backed <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/> for
///     intent centroids. Eviction is threat-driven: high threat-score entries are
///     retained longest; low-threat entries are shed first when the hot tier hits
///     its cap. Uses <c>botProbability: 0.0</c> so only the threat dimension drives
///     the <c>DecisionNecessity.ColdnessScore</c> (intent has no binary bot-score).
/// </summary>
public sealed class SqliteIntentCentroidStore
    : WriteBehindLfuStore<string, IntentCentroidEntry, IntentCentroidWriteOp>,
      IIntentCentroidStore,
      IStoreInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteIntentCentroidStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _initTask;

    private const double HalfLifeSeconds = 604_800.0; // 7 days
    private const double Threshold        = 0.70;

    // ctor for DI (primary path; derives own .db file from DatabasePath).
    public SqliteIntentCentroidStore(
        IOptions<BotDetectionOptions> options,
        ILogger<SqliteIntentCentroidStore> logger)
        : base(
            maxEntries: options.Value.SelfMaintenance.IntentCacheSize,
            writeQueueCapacity: options.Value.SelfMaintenance.IntentCacheSize * 2,
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
        _connectionString = $"Data Source={Path.Combine(basePath, "intent_centroids.db")};Cache=Shared";
    }

    // ctor for test backward compat (accepts explicit connection string; uses default caps).
    public SqliteIntentCentroidStore(
        string connectionString,
        ILogger<SqliteIntentCentroidStore> logger)
        : base(
            maxEntries: 1_000,
            writeQueueCapacity: 2_000,
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
            CREATE TABLE IF NOT EXISTS intent_centroids (
                signature_id    TEXT PRIMARY KEY,
                vector          BLOB    NOT NULL,
                threat_score    REAL    NOT NULL DEFAULT 0.0,
                intent_category TEXT    NOT NULL DEFAULT 'unknown',
                updated_at      INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_intc_updated ON intent_centroids(updated_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Hot-path facade (non-blocking; for Task B Slim* wiring) ───────────

    public void RecordIntent(string signatureId, float[] vector, double threatScore, string category)
    {
        if (string.IsNullOrEmpty(signatureId)) return;
        Record(signatureId, new IntentCentroidWriteOp(signatureId, vector, threatScore, category));
    }

    // ── IIntentCentroidStore ───────────────────────────────────────────────

    /// <summary>
    ///     Synchronous one-shot durable upsert (tests/admin). The hot path uses
    ///     <see cref="RecordIntent"/> (write-behind); do not call this per-request.
    /// </summary>
    public async Task UpsertIntentAsync(
        string signatureId, float[] vector, double threatScore, string intentCategory,
        CancellationToken ct = default)
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
                    INSERT INTO intent_centroids (signature_id, vector, threat_score, intent_category, updated_at)
                    VALUES (@sig, @vec, @ts_score, @cat, @ts)
                    ON CONFLICT(signature_id) DO UPDATE SET
                        vector=excluded.vector, threat_score=excluded.threat_score,
                        intent_category=excluded.intent_category, updated_at=excluded.updated_at;
                    """;
                cmd.Parameters.AddWithValue("@sig",      signatureId);
                cmd.Parameters.AddWithValue("@vec",      CentroidFloatPacker.Pack(vector));
                cmd.Parameters.AddWithValue("@ts_score", threatScore);
                cmd.Parameters.AddWithValue("@cat",      intentCategory);
                cmd.Parameters.AddWithValue("@ts",       DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally { _writeLock.Release(); }
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertIntent direct write failed for {Sig}", signatureId); }
    }

    public async Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(
        int limit, CancellationToken ct = default)
    {
        var result = new List<IntentCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, threat_score, intent_category
                FROM intent_centroids ORDER BY updated_at DESC LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new IntentCentroidRow(
                    reader.GetString(0),
                    CentroidFloatPacker.Unpack((byte[])reader[1]),
                    reader.GetDouble(2),
                    reader.GetString(3)));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentIntents failed"); }
        return result;
    }

    public async Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM intent_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoffEpochSeconds);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} intent centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneIntents failed"); }
    }

    // ── WriteBehindLfuStore hooks ──────────────────────────────────────────

    protected override IntentCentroidEntry CreateInitial(string key, IntentCentroidWriteOp op) =>
        new(op.SignatureId, op.Vector, op.ThreatScore, op.IntentCategory,
            UpdatedAtTicks: DateTimeOffset.UtcNow.UtcTicks);

    protected override IntentCentroidEntry MergeIntoExisting(
        string key, IntentCentroidEntry existing, IntentCentroidWriteOp op) =>
        existing with
        {
            Vector         = op.Vector,
            ThreatScore    = op.ThreatScore,
            IntentCategory = op.IntentCategory,
            UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks
        };

    // Intent uses threat-only: botProbability=0.0 so only threat and recency matter.
    protected override long ColdnessScore(IntentCentroidEntry entry)
    {
        var ageSeconds = (DateTimeOffset.UtcNow.UtcTicks - entry.UpdatedAtTicks)
                         / (double)TimeSpan.TicksPerSecond;
        return DecisionNecessity.ColdnessScore(
            botProbability:  0.0,
            threat:          entry.ThreatScore,
            ageSeconds:      ageSeconds,
            threshold:       Threshold,
            halfLifeSeconds: HalfLifeSeconds);
    }

    /// <summary>Exposes the LFU coldness ranking for the hot entry at <paramref name="key"/>.</summary>
    public long GetColdnessScore(string key)
    {
        var entry = TryGetHot(key);
        return entry is null ? 0L : ColdnessScore(entry);
    }

    protected override async ValueTask<IntentCentroidEntry?> LoadFromDurableTierAsync(
        string key, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, threat_score, intent_category, updated_at
                FROM intent_centroids WHERE signature_id = @sig
                """;
            cmd.Parameters.AddWithValue("@sig", key);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var updatedAt = reader.GetInt64(4);
            return new IntentCentroidEntry(
                reader.GetString(0),
                CentroidFloatPacker.Unpack((byte[])reader[1]),
                reader.GetDouble(2),
                reader.GetString(3),
                UpdatedAtTicks: DateTimeOffset.FromUnixTimeSeconds(updatedAt).UtcTicks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LoadFromDurableTier failed for intent {Key}", key);
            return null;
        }
    }

    protected override async Task PersistBatchAsync(
        IReadOnlyList<IntentCentroidWriteOp> batch, CancellationToken ct)
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
                INSERT INTO intent_centroids (signature_id, vector, threat_score, intent_category, updated_at)
                VALUES ($sig, $vec, $ts_score, $cat, $ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, threat_score=excluded.threat_score,
                    intent_category=excluded.intent_category, updated_at=excluded.updated_at
                """;
            var pSig   = cmd.CreateParameter(); pSig.ParameterName   = "$sig";      cmd.Parameters.Add(pSig);
            var pVec   = cmd.CreateParameter(); pVec.ParameterName   = "$vec";      cmd.Parameters.Add(pVec);
            var pScore = cmd.CreateParameter(); pScore.ParameterName = "$ts_score"; cmd.Parameters.Add(pScore);
            var pCat   = cmd.CreateParameter(); pCat.ParameterName   = "$cat";      cmd.Parameters.Add(pCat);
            var pTs    = cmd.CreateParameter(); pTs.ParameterName    = "$ts";       cmd.Parameters.Add(pTs);

            var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var op in batch)
            {
                pSig.Value   = op.SignatureId;
                pVec.Value   = CentroidFloatPacker.Pack(op.Vector);
                pScore.Value = op.ThreatScore;
                pCat.Value   = op.IntentCategory;
                pTs.Value    = nowEpoch;
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
