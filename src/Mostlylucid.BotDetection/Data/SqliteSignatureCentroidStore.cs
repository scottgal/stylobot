using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Aggregate value held per signature in the hot tier.
///     <c>UpdatedAtTicks</c> drives the <c>DecisionNecessity.ColdnessScore</c>
///     recency factor so that stale entries are shed under memory pressure first.
/// </summary>
public sealed record SignatureCentroidEntry(
    string SignatureId,
    float[] Vector,
    bool WasBot,
    double Confidence,
    int AccessCount,
    long UpdatedAtTicks);

/// <summary>Discrete write op fed into the <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/> drainer.</summary>
public sealed record SignatureCentroidWriteOp(
    string SignatureId,
    float[] Vector,
    bool WasBot,
    double Confidence);

/// <summary>
///     SQLite-backed <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/> for
///     signature centroids. The hot tier serves per-request cosine lookups;
///     the drainer batches inserts to <c>signature_centroids.db</c> so restarts
///     re-warm from the durable tier via <see cref="LoadFromDurableTierAsync"/>.
///     <para>
///     Eviction priority is <c>DecisionNecessity.ColdnessScore</c>: entries near
///     the detection threshold or with high threat are retained longest; certain,
///     harmless entries are shed first when the hot tier hits its cap.
///     </para>
/// </summary>
public sealed class SqliteSignatureCentroidStore
    : WriteBehindLfuStore<string, SignatureCentroidEntry, SignatureCentroidWriteOp>,
      ISignatureCentroidStore,
      IStoreInitializer
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSignatureCentroidStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _initTask;

    private const double HalfLifeSeconds = 604_800.0; // 7 days
    private const double Threshold        = 0.70;

    // ctor for DI (primary path; derives own .db file from DatabasePath).
    public SqliteSignatureCentroidStore(
        IOptions<BotDetectionOptions> options,
        ILogger<SqliteSignatureCentroidStore> logger)
        : base(
            maxEntries: options.Value.SelfMaintenance.SignatureCacheSize,
            writeQueueCapacity: options.Value.SelfMaintenance.SignatureCacheSize * 2,
            batchMaxSize: 256,
            drainInterval: TimeSpan.FromMilliseconds(500),
            logger: logger,
            keyComparer: StringComparer.Ordinal)
    {
        _logger = logger;
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        _connectionString = $"Data Source={Path.Combine(basePath, "signature_centroids.db")};Cache=Shared";
    }

    // ctor for test backward compat (accepts explicit connection string; uses default caps).
    public SqliteSignatureCentroidStore(
        string connectionString,
        ILogger<SqliteSignatureCentroidStore> logger)
        : base(
            maxEntries: 5_000,
            writeQueueCapacity: 10_000,
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
            CREATE TABLE IF NOT EXISTS signature_centroids (
                signature_id TEXT PRIMARY KEY,
                vector       BLOB    NOT NULL,
                was_bot      INTEGER NOT NULL DEFAULT 0,
                confidence   REAL    NOT NULL DEFAULT 0.5,
                access_count INTEGER NOT NULL DEFAULT 0,
                updated_at   INTEGER NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_sigc_updated ON signature_centroids(updated_at);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ── Hot-path facade (non-blocking; for Task B Slim* wiring) ───────────

    public void RecordSignature(string signatureId, float[] vector, bool wasBot, double confidence)
    {
        if (string.IsNullOrEmpty(signatureId)) return;
        Record(signatureId, new SignatureCentroidWriteOp(signatureId, vector, wasBot, confidence));
    }

    // ── ISignatureCentroidStore ────────────────────────────────────────────

    /// <summary>
    ///     Updates the hot cache immediately via <see cref="RecordSignature"/> and
    ///     also writes through to SQLite so <see cref="GetRecentSignaturesAsync"/>
    ///     sees the data without waiting for the drainer.
    /// </summary>
    public async Task UpsertSignatureAsync(
        string signatureId, float[] vector, bool wasBot, double confidence,
        CancellationToken ct = default)
    {
        RecordSignature(signatureId, vector, wasBot, confidence);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await UpsertSignatureDirectAsync(conn, signatureId, vector, wasBot, confidence, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertSignature direct write failed for {Sig}", signatureId); }
    }

    /// <summary>Shared-connection overload; kept for <c>SqliteCentroidWriter</c> back-compat (Task B deletion).</summary>
    public async Task UpsertSignatureAsync(
        SqliteConnection conn, string signatureId, float[] vector, bool wasBot, double confidence,
        CancellationToken ct = default)
    {
        RecordSignature(signatureId, vector, wasBot, confidence);
        await UpsertSignatureDirectAsync(conn, signatureId, vector, wasBot, confidence, ct);
    }

    private static async Task UpsertSignatureDirectAsync(
        SqliteConnection conn, string signatureId, float[] vector, bool wasBot, double confidence,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO signature_centroids (signature_id, vector, was_bot, confidence, access_count, updated_at)
            VALUES (@sig, @vec, @bot, @conf, 1, @ts)
            ON CONFLICT(signature_id) DO UPDATE SET
                vector=excluded.vector, was_bot=excluded.was_bot,
                confidence=excluded.confidence,
                access_count=signature_centroids.access_count + 1,
                updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@sig", signatureId);
        cmd.Parameters.AddWithValue("@vec", CentroidFloatPacker.Pack(vector));
        cmd.Parameters.AddWithValue("@bot", wasBot ? 1 : 0);
        cmd.Parameters.AddWithValue("@conf", confidence);
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(
        int limit, CancellationToken ct = default)
    {
        var result = new List<SignatureCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, was_bot, confidence, access_count
                FROM signature_centroids ORDER BY updated_at DESC LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new SignatureCentroidRow(
                    reader.GetString(0),
                    CentroidFloatPacker.Unpack((byte[])reader[1]),
                    reader.GetInt32(2) != 0,
                    reader.GetDouble(3),
                    reader.GetInt32(4)));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentSignatures failed"); }
        return result;
    }

    public async Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM signature_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoffEpochSeconds);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} signature centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneSignatures failed"); }
    }

    // ── WriteBehindLfuStore hooks ──────────────────────────────────────────

    protected override SignatureCentroidEntry CreateInitial(string key, SignatureCentroidWriteOp op) =>
        new(op.SignatureId, op.Vector, op.WasBot, op.Confidence,
            AccessCount: 1,
            UpdatedAtTicks: DateTimeOffset.UtcNow.UtcTicks);

    protected override SignatureCentroidEntry MergeIntoExisting(
        string key, SignatureCentroidEntry existing, SignatureCentroidWriteOp op) =>
        existing with
        {
            Vector         = op.Vector,
            WasBot         = op.WasBot,
            Confidence     = op.Confidence,
            AccessCount    = existing.AccessCount + 1,
            UpdatedAtTicks = DateTimeOffset.UtcNow.UtcTicks
        };

    protected override long ColdnessScore(SignatureCentroidEntry entry)
    {
        var ageSeconds = (DateTimeOffset.UtcNow.UtcTicks - entry.UpdatedAtTicks)
                         / (double)TimeSpan.TicksPerSecond;
        return DecisionNecessity.ColdnessScore(
            botProbability: entry.Confidence,
            threat:         entry.WasBot ? entry.Confidence : 0.0,
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

    protected override async ValueTask<SignatureCentroidEntry?> LoadFromDurableTierAsync(
        string key, CancellationToken ct)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, was_bot, confidence, access_count, updated_at
                FROM signature_centroids WHERE signature_id = @sig
                """;
            cmd.Parameters.AddWithValue("@sig", key);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            var updatedAt = reader.GetInt64(5);
            return new SignatureCentroidEntry(
                reader.GetString(0),
                CentroidFloatPacker.Unpack((byte[])reader[1]),
                reader.GetInt32(2) != 0,
                reader.GetDouble(3),
                reader.GetInt32(4),
                UpdatedAtTicks: DateTimeOffset.FromUnixTimeSeconds(updatedAt).UtcTicks);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "LoadFromDurableTier failed for signature {Key}", key);
            return null;
        }
    }

    protected override async Task PersistBatchAsync(
        IReadOnlyList<SignatureCentroidWriteOp> batch, CancellationToken ct)
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
                INSERT INTO signature_centroids (signature_id, vector, was_bot, confidence, access_count, updated_at)
                VALUES ($sig, $vec, $bot, $conf, 1, $ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, was_bot=excluded.was_bot,
                    confidence=excluded.confidence,
                    updated_at=excluded.updated_at
                """;
            var pSig  = cmd.CreateParameter(); pSig.ParameterName  = "$sig";  cmd.Parameters.Add(pSig);
            var pVec  = cmd.CreateParameter(); pVec.ParameterName  = "$vec";  cmd.Parameters.Add(pVec);
            var pBot  = cmd.CreateParameter(); pBot.ParameterName  = "$bot";  cmd.Parameters.Add(pBot);
            var pConf = cmd.CreateParameter(); pConf.ParameterName = "$conf"; cmd.Parameters.Add(pConf);
            var pTs   = cmd.CreateParameter(); pTs.ParameterName   = "$ts";   cmd.Parameters.Add(pTs);

            var nowEpoch = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            foreach (var op in batch)
            {
                pSig.Value  = op.SignatureId;
                pVec.Value  = CentroidFloatPacker.Pack(op.Vector);
                pBot.Value  = op.WasBot ? 1 : 0;
                pConf.Value = op.Confidence;
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
