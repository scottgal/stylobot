using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Actions;

/// <summary>
///     Discrete violation event enqueued for the drainer. The hot tier
///     keeps the current <see cref="StickyDenyState"/>; the drainer
///     persists the latest snapshot per key on its own schedule.
/// </summary>
public sealed record StickyDenyViolation(string Key, DateTimeOffset Timestamp);

/// <summary>
///     SQLite-backed <see cref="IStickyDenyTracker"/> on the canonical
///     <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/> pattern.
///     Hot tier serves IsBlocked / RecordViolation in microseconds; the
///     drainer batches snapshots into <c>sticky_deny.db</c> so the block
///     window survives a process restart. Without persistence the bot
///     just retries after a restart and escapes its block, defeating
///     the policy.
///     <para>
///     Schema: one row per key with the latest violation counters and
///     the wall-clock <c>blocked_until_ticks</c>. Upsert via ON CONFLICT
///     so retrying a batch is idempotent. Cold load reads the latest
///     snapshot per key when the hot tier missed it (the typical
///     restart-recovery path).
///     </para>
/// </summary>
public sealed class SqliteStickyDenyStore
    : WriteBehindLfuStore<string, StickyDenyState, StickyDenyViolation>,
      IStickyDenyTracker,
      Mostlylucid.BotDetection.Storage.IStoreInitializer
{
    private readonly StickyDenyActionOptions _options;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _initTask;

    public SqliteStickyDenyStore(
        IOptions<BotDetectionOptions> options,
        StickyDenyActionOptions stickyOptions,
        ILogger<SqliteStickyDenyStore> logger)
        : base(
            maxEntries: 8192,
            writeQueueCapacity: 16384,
            batchMaxSize: 256,
            drainInterval: TimeSpan.FromSeconds(1),
            logger: logger,
            keyComparer: StringComparer.Ordinal)
    {
        _options = stickyOptions;

        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        var dbPath = Path.Combine(basePath, "sticky_deny.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    public Task InitializeAsync(CancellationToken ct = default) =>
        _initTask ??= InitializeOnceAsync(ct);

    private async Task InitializeOnceAsync(CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // DDL lives in Data/Schema/sticky_deny.sql so it diffs / lints /
        // formats like SQL instead of a multiline C# string literal.
        cmd.CommandText = Mostlylucid.BotDetection.Data.Schema.SchemaLoader.Load("sticky_deny");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // === IStickyDenyTracker (hot path) ====================================

    public bool IsBlocked(string key, DateTimeOffset now)
    {
        var s = TryGetHot(key);
        return s is not null && s.BlockedUntilTicks > now.UtcTicks;
    }

    public bool RecordViolation(string key, DateTimeOffset now)
    {
        // Snapshot the previous state so the caller can observe the
        // "now blocked" transition. Record() calls CreateInitial when no
        // entry exists or MergeIntoExisting otherwise -- both produce the
        // post-violation state. The transition flag is the diff.
        var prior = TryGetHot(key);
        var updated = Record(key, new StickyDenyViolation(key, now));

        var wasBlocked = prior is not null && prior.BlockedUntilTicks > now.UtcTicks;
        var nowBlocked = updated.BlockedUntilTicks > now.UtcTicks;
        return !wasBlocked && nowBlocked;
    }

    // === WriteBehindLfuStore hooks ========================================

    protected override StickyDenyState CreateInitial(string key, StickyDenyViolation op)
    {
        var blockUntil = _options.ViolationThreshold <= 1
            ? op.Timestamp.UtcTicks + TimeSpan.FromSeconds(_options.BlockTtlSeconds).Ticks
            : 0;
        return new StickyDenyState(
            FirstViolationTicks: op.Timestamp.UtcTicks,
            LastViolationTicks: op.Timestamp.UtcTicks,
            ViolationCount: 1,
            BlockedUntilTicks: blockUntil);
    }

    protected override StickyDenyState MergeIntoExisting(string key, StickyDenyState existing, StickyDenyViolation op)
    {
        var windowStartTicks = op.Timestamp.UtcTicks - TimeSpan.FromSeconds(_options.WindowSeconds).Ticks;
        int newCount;
        long newFirst;
        if (existing.FirstViolationTicks < windowStartTicks)
        {
            // Window closed: count resets to this violation.
            newCount = 1;
            newFirst = op.Timestamp.UtcTicks;
        }
        else
        {
            newCount = existing.ViolationCount + 1;
            newFirst = existing.FirstViolationTicks;
        }

        var crossedThreshold = newCount >= _options.ViolationThreshold;
        // Extend the block only on a fresh threshold crossing; otherwise
        // preserve whatever block window was already in effect.
        var blockUntil = crossedThreshold
            ? op.Timestamp.UtcTicks + TimeSpan.FromSeconds(_options.BlockTtlSeconds).Ticks
            : existing.BlockedUntilTicks;

        return new StickyDenyState(
            FirstViolationTicks: newFirst,
            LastViolationTicks: op.Timestamp.UtcTicks,
            ViolationCount: newCount,
            BlockedUntilTicks: blockUntil);
    }

    /// <summary>
    ///     Newest-last-violation = hottest. Long-idle keys evict first.
    /// </summary>
    protected override long ColdnessScore(StickyDenyState entry) => entry.LastViolationTicks;

    protected override async ValueTask<StickyDenyState?> LoadFromDurableTierAsync(string key, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT first_violation_ticks, last_violation_ticks, violation_count, blocked_until_ticks
            FROM sticky_deny
            WHERE key = $key
            """;
        cmd.Parameters.AddWithValue("$key", key);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new StickyDenyState(
            FirstViolationTicks: reader.GetInt64(0),
            LastViolationTicks: reader.GetInt64(1),
            ViolationCount: reader.GetInt32(2),
            BlockedUntilTicks: reader.GetInt64(3));
    }

    protected override async Task PersistBatchAsync(IReadOnlyList<StickyDenyViolation> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        // We persist the LATEST snapshot per key (rather than the discrete
        // violation event) because the hot dict already holds the current
        // aggregate. Re-deriving from events at cold-load would force us to
        // store every violation forever; storing the snapshot keeps the
        // table size bounded by distinct keys.
        var distinctKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var op in batch) distinctKeys.Add(op.Key);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO sticky_deny
                    (key, first_violation_ticks, last_violation_ticks, violation_count, blocked_until_ticks)
                VALUES ($key, $first, $last, $count, $blockUntil)
                ON CONFLICT (key) DO UPDATE SET
                    first_violation_ticks = excluded.first_violation_ticks,
                    last_violation_ticks = excluded.last_violation_ticks,
                    violation_count = excluded.violation_count,
                    blocked_until_ticks = excluded.blocked_until_ticks
                """;
            var pKey = cmd.CreateParameter(); pKey.ParameterName = "$key"; cmd.Parameters.Add(pKey);
            var pFirst = cmd.CreateParameter(); pFirst.ParameterName = "$first"; cmd.Parameters.Add(pFirst);
            var pLast = cmd.CreateParameter(); pLast.ParameterName = "$last"; cmd.Parameters.Add(pLast);
            var pCount = cmd.CreateParameter(); pCount.ParameterName = "$count"; cmd.Parameters.Add(pCount);
            var pBlock = cmd.CreateParameter(); pBlock.ParameterName = "$blockUntil"; cmd.Parameters.Add(pBlock);

            foreach (var key in distinctKeys)
            {
                var state = TryGetHot(key);
                if (state is null) continue; // evicted between enqueue and flush; skip
                pKey.Value = key;
                pFirst.Value = state.FirstViolationTicks;
                pLast.Value = state.LastViolationTicks;
                pCount.Value = state.ViolationCount;
                pBlock.Value = state.BlockedUntilTicks;
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
