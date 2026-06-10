using System.Collections.Immutable;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Per-(ip, session, ts) observation; the discrete write op fed into the
///     <see cref="WriteBehindLfuStore{TKey, TValue, TWriteOp}"/> drainer.
/// </summary>
public sealed record PoolObservation(
    string ShapeHash,
    string IpHash,
    string SessionId,
    DateTimeOffset Timestamp);

/// <summary>
///     Aggregate value held per shape hash in the hot tier. Immutable so
///     <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}.MergeIntoExisting"/>
///     can be retried safely under concurrent updates.
/// </summary>
public sealed record ShapeBucket(
    ImmutableDictionary<string, DateTimeOffset> Observations,
    long LastSeenTicks)
{
    public static ShapeBucket Empty => new(
        ImmutableDictionary<string, DateTimeOffset>.Empty.WithComparers(StringComparer.Ordinal),
        0);

    /// <summary>Compose the (ip + session) key used inside the bucket.</summary>
    public static string ContextKey(string ipHash, string sessionId) => $"{ipHash}|{sessionId}";

    public int DistinctContextsSince(DateTimeOffset since)
    {
        var count = 0;
        foreach (var ts in Observations.Values)
            if (ts >= since) count++;
        return count;
    }
}

/// <summary>
///     SQLite-backed <see cref="WriteBehindLfuStore{TKey, TValue, TWriteOp}"/>
///     subclass for fingerprint-pool collision tracking. The hot tier serves
///     the per-request collision count check; the SQLite drainer persists
///     observations so cross-restart durability and dashboard surfacing work.
///     <para>
///     Schema: one row per (shape_hash, context_key) with the latest observed
///     timestamp. Upsert via ON CONFLICT DO UPDATE so retrying a batch is
///     idempotent. Index on (shape_hash, last_seen_ts) backs the cold load's
///     window query.
///     </para>
/// </summary>
public sealed class SqlitePoolCollisionStore
    : WriteBehindLfuStore<string, ShapeBucket, PoolObservation>,
      IFingerprintPoolCollisionTracker
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _initTask;

    /// <summary>
    ///     Window applied during cold load. Observations older than this are
    ///     not rehydrated into the hot bucket -- collision detection cares
    ///     about recent activity, not stale entries.
    /// </summary>
    public TimeSpan ColdLoadWindow { get; init; } = TimeSpan.FromHours(6);

    public SqlitePoolCollisionStore(
        IOptions<BotDetectionOptions> options,
        ILogger<SqlitePoolCollisionStore> logger)
        : base(
            maxEntries: 4096,
            writeQueueCapacity: 8192,
            batchMaxSize: 256,
            drainInterval: TimeSpan.FromMilliseconds(500),
            logger: logger,
            keyComparer: StringComparer.Ordinal)
    {
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        var dbPath = Path.Combine(basePath, "pool_collisions.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    public int TrackedShapeCount => Count;

    public Task InitializeAsync(CancellationToken ct = default) =>
        _initTask ??= InitializeOnceAsync(ct);

    private async Task InitializeOnceAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = Mostlylucid.BotDetection.Data.Schema.SchemaLoader.Load("pool_collisions");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // === IFingerprintPoolCollisionTracker (hot-path facade) ================

    public void Observe(string shapeHash, string ipHash, string sessionId, DateTimeOffset utcNow)
    {
        if (string.IsNullOrEmpty(shapeHash)) return;
        Record(shapeHash, new PoolObservation(shapeHash, ipHash ?? "", sessionId ?? "", utcNow));
    }

    public int DistinctContextsInWindow(string shapeHash, DateTimeOffset since)
    {
        if (string.IsNullOrEmpty(shapeHash)) return 0;
        var hot = TryGetHot(shapeHash);
        return hot?.DistinctContextsSince(since) ?? 0;
    }

    // === WriteBehindLfuStore hooks =========================================

    protected override ShapeBucket CreateInitial(string key, PoolObservation op)
    {
        var ctxKey = ShapeBucket.ContextKey(op.IpHash, op.SessionId);
        return new ShapeBucket(
            ShapeBucket.Empty.Observations.SetItem(ctxKey, op.Timestamp),
            op.Timestamp.UtcTicks);
    }

    protected override ShapeBucket MergeIntoExisting(string key, ShapeBucket existing, PoolObservation op)
    {
        var ctxKey = ShapeBucket.ContextKey(op.IpHash, op.SessionId);
        var updated = existing.Observations.SetItem(ctxKey, op.Timestamp);
        var newLast = Math.Max(existing.LastSeenTicks, op.Timestamp.UtcTicks);
        return new ShapeBucket(updated, newLast);
    }

    /// <summary>
    ///     LFU coldness ranking: oldest activity = coldest = evicted first.
    ///     Recent observations stay hot; long-idle shapes get evicted under
    ///     memory pressure.
    /// </summary>
    protected override long ColdnessScore(ShapeBucket entry) => entry.LastSeenTicks;

    protected override async ValueTask<ShapeBucket?> LoadFromDurableTierAsync(string key, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow - ColdLoadWindow;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT context_key, last_seen_ticks
            FROM pool_collisions
            WHERE shape_hash = $shape AND last_seen_ticks >= $since
            """;
        cmd.Parameters.AddWithValue("$shape", key);
        cmd.Parameters.AddWithValue("$since", since.UtcTicks);

        var dict = ShapeBucket.Empty.Observations;
        long lastSeen = 0;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var ctxKey = reader.GetString(0);
            var ticks = reader.GetInt64(1);
            dict = dict.SetItem(ctxKey, new DateTimeOffset(ticks, TimeSpan.Zero));
            if (ticks > lastSeen) lastSeen = ticks;
        }
        if (dict.IsEmpty) return null;
        return new ShapeBucket(dict, lastSeen);
    }

    protected override async Task PersistBatchAsync(IReadOnlyList<PoolObservation> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        // Single writer guarded by semaphore -- SQLite's WAL mode handles
        // concurrent readers fine but multiple writer connections serialise
        // on a file lock anyway. The semaphore makes the contention explicit.
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO pool_collisions (shape_hash, context_key, last_seen_ticks)
                VALUES ($shape, $ctx, $ts)
                ON CONFLICT (shape_hash, context_key) DO UPDATE SET
                    last_seen_ticks = MAX(last_seen_ticks, excluded.last_seen_ticks)
                """;
            var pShape = cmd.CreateParameter(); pShape.ParameterName = "$shape"; cmd.Parameters.Add(pShape);
            var pCtx = cmd.CreateParameter(); pCtx.ParameterName = "$ctx"; cmd.Parameters.Add(pCtx);
            var pTs = cmd.CreateParameter(); pTs.ParameterName = "$ts"; cmd.Parameters.Add(pTs);

            foreach (var op in batch)
            {
                pShape.Value = op.ShapeHash;
                pCtx.Value = ShapeBucket.ContextKey(op.IpHash, op.SessionId);
                pTs.Value = op.Timestamp.UtcTicks;
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
