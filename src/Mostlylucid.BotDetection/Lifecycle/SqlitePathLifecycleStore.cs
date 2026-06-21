using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     Discrete write op enqueued for batched persistence. The drainer folds
///     a batch of these into SQLite UPSERTs in a single transaction; the merge
///     into the hot tier is handled by <see cref="SqlitePathLifecycleStore.MergeIntoExisting"/>.
/// </summary>
public sealed record PathLifecycleOp(string Path, int StatusCode, DateTime UtcNow);

/// <summary>
///     SQLite-backed <see cref="WriteBehindLfuStore{TKey,TValue,TWriteOp}"/>
///     for per-path response history. The hot tier (ConcurrentDictionary) is
///     authoritative for the live counters; the drainer flushes batched
///     UPSERTs into SQLite for cross-restart durability.
/// </summary>
/// <remarks>
///     <para>
///         The base class handles batching, LFU eviction, bounded channel
///         back-pressure (DropOldest), and the single background drainer.
///         <see cref="RecordResponseAsync"/> is a synchronous in-memory op
///         that returns in microseconds.
///     </para>
///     <para>
///         Adaptive behaviour comes from the base: under sustained write load
///         the drainer fills batches up to <c>batchMaxSize</c> per cycle (one
///         transaction per batch); under quiet periods partial batches flush
///         after <c>drainInterval</c>. The channel's <c>DropOldest</c> mode
///         provides automatic back-pressure -- if the durable tier can't keep
///         up, the oldest queued op is dropped (the hot tier still has the
///         latest aggregated state; only the persistence catch-up is lost).
///     </para>
///     <para>
///         Static asset paths (CSS/JS/images/fonts) are filtered at the
///         caller -- they would dominate the table without adding signal.
///     </para>
/// </remarks>
public sealed class SqlitePathLifecycleStore
    : WriteBehindLfuStore<string, PathLifecycle, PathLifecycleOp>,
      IPathLifecycleStore
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    public SqlitePathLifecycleStore(
        string connectionString,
        ILogger<SqlitePathLifecycleStore> logger,
        PathLifecycleOptions? opts = null)
        : base(
            maxEntries: (opts ?? new PathLifecycleOptions()).MaxEntries,
            writeQueueCapacity: (opts ?? new PathLifecycleOptions()).WriteQueueCapacity,
            batchMaxSize: (opts ?? new PathLifecycleOptions()).BatchMaxSize,
            drainInterval: (opts ?? new PathLifecycleOptions()).DrainInterval,
            logger: logger,
            keyComparer: StringComparer.Ordinal)
    {
        _connectionString = connectionString;
    }

    // === IPathLifecycleStore facade ========================================

    /// <summary>
    ///     Hot-path write: in-memory only. Returns immediately; persistence
    ///     happens out-of-band via the base-class drainer. <paramref name="ct"/>
    ///     is ignored -- the work is bounded local memory ops.
    /// </summary>
    public Task RecordResponseAsync(string path, int statusCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;
        Record(path, new PathLifecycleOp(path, statusCode, DateTime.UtcNow));
        return Task.CompletedTask;
    }

    public async Task<PathLifecycle?> GetAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return await GetAsync((string)path, ct);   // disambiguate from base.GetAsync(TKey)
    }

    /// <summary>
    ///     Test-only: drain any pending writes synchronously. Production
    ///     code never calls this -- the drainer handles persistence on its
    ///     own schedule.
    /// </summary>
    internal Task FlushAsync(CancellationToken ct = default) => FlushPendingAsync(ct);

    // === WriteBehindLfuStore hooks =========================================

    protected override PathLifecycle CreateInitial(string path, PathLifecycleOp op)
    {
        var is2xx = op.StatusCode is >= 200 and < 300;
        var is4xx = op.StatusCode is >= 400 and < 500;
        return new PathLifecycle
        {
            Path = path,
            FirstSeenUtc = op.UtcNow,
            LastSeenUtc = op.UtcNow,
            Total2xx = is2xx ? 1 : 0,
            Total4xx = is4xx ? 1 : 0,
            TotalOther = (!is2xx && !is4xx) ? 1 : 0,
            Last2xxUtc = is2xx ? op.UtcNow : null,
            First4xxAfter2xxUtc = null
        };
    }

    protected override PathLifecycle MergeIntoExisting(string path, PathLifecycle prev, PathLifecycleOp op)
    {
        var is2xx = op.StatusCode is >= 200 and < 300;
        var is4xx = op.StatusCode is >= 400 and < 500;
        return prev with
        {
            LastSeenUtc = op.UtcNow,
            Total2xx = prev.Total2xx + (is2xx ? 1 : 0),
            Total4xx = prev.Total4xx + (is4xx ? 1 : 0),
            TotalOther = prev.TotalOther + ((!is2xx && !is4xx) ? 1 : 0),
            Last2xxUtc = is2xx ? op.UtcNow : prev.Last2xxUtc,
            // Lock in the first 4xx that lands AFTER any 2xx history.
            First4xxAfter2xxUtc =
                is4xx && prev.Total2xx > 0 && !prev.First4xxAfter2xxUtc.HasValue
                    ? op.UtcNow
                    : prev.First4xxAfter2xxUtc
        };
    }

    /// <summary>
    ///     LFU coldness: oldest last-seen = coldest = evicted first. Recently
    ///     active paths stay in the hot tier; long-idle paths get evicted under
    ///     memory pressure and reload from SQLite on next access.
    /// </summary>
    protected override long ColdnessScore(PathLifecycle entry) => entry.LastSeenUtc.Ticks;

    protected override async ValueTask<PathLifecycle?> LoadFromDurableTierAsync(string path, CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT first_seen_utc, total_2xx, total_4xx, total_other,
                       last_2xx_utc, first_4xx_after_2xx_utc, last_seen_utc
                FROM path_lifecycle WHERE path = @path
                """;
            cmd.Parameters.AddWithValue("@path", path);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct)) return null;
            return new PathLifecycle
            {
                Path = path,
                FirstSeenUtc = ParseUtc(reader.GetString(0)),
                Total2xx = reader.GetInt32(1),
                Total4xx = reader.GetInt32(2),
                TotalOther = reader.GetInt32(3),
                Last2xxUtc = reader.IsDBNull(4) ? null : ParseUtc(reader.GetString(4)),
                First4xxAfter2xxUtc = reader.IsDBNull(5) ? null : ParseUtc(reader.GetString(5)),
                LastSeenUtc = ParseUtc(reader.GetString(6))
            };
        }
        catch (Exception ex)
        {
            ((ILogger)typeof(SqlitePathLifecycleStore)).LogDebug(ex, "PathLifecycle cold load failed for {Path}", path);
            return null;
        }
    }

    protected override async Task PersistBatchAsync(IReadOnlyList<PathLifecycleOp> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;
        await EnsureInitializedAsync(ct);

        // Dedupe by path: many ops in a single batch can land on the same path
        // under load. We only need to UPSERT the current aggregated value once
        // per unique path -- the hot tier already holds the merged result.
        var keys = new HashSet<string>(batch.Count, StringComparer.Ordinal);
        foreach (var op in batch) keys.Add(op.Path);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO path_lifecycle
                    (path, first_seen_utc, total_2xx, total_4xx, total_other,
                     last_2xx_utc, first_4xx_after_2xx_utc, last_seen_utc)
                VALUES
                    (@path, @first_seen, @t2, @t4, @to, @last2, @first4, @last_seen)
                ON CONFLICT(path) DO UPDATE SET
                    total_2xx = excluded.total_2xx,
                    total_4xx = excluded.total_4xx,
                    total_other = excluded.total_other,
                    last_2xx_utc = excluded.last_2xx_utc,
                    first_4xx_after_2xx_utc = COALESCE(path_lifecycle.first_4xx_after_2xx_utc, excluded.first_4xx_after_2xx_utc),
                    last_seen_utc = excluded.last_seen_utc
                """;
            var pPath = cmd.Parameters.Add("@path", SqliteType.Text);
            var pFirst = cmd.Parameters.Add("@first_seen", SqliteType.Text);
            var pT2 = cmd.Parameters.Add("@t2", SqliteType.Integer);
            var pT4 = cmd.Parameters.Add("@t4", SqliteType.Integer);
            var pTo = cmd.Parameters.Add("@to", SqliteType.Integer);
            var pLast2 = cmd.Parameters.Add("@last2", SqliteType.Text);
            var pFirst4 = cmd.Parameters.Add("@first4", SqliteType.Text);
            var pLastSeen = cmd.Parameters.Add("@last_seen", SqliteType.Text);

            foreach (var path in keys)
            {
                var entry = TryGetHot(path);
                if (entry is null) continue;
                pPath.Value = entry.Path;
                pFirst.Value = entry.FirstSeenUtc.ToString("O");
                pT2.Value = entry.Total2xx;
                pT4.Value = entry.Total4xx;
                pTo.Value = entry.TotalOther;
                pLast2.Value = (object?)entry.Last2xxUtc?.ToString("O") ?? DBNull.Value;
                pFirst4.Value = (object?)entry.First4xxAfter2xxUtc?.ToString("O") ?? DBNull.Value;
                pLastSeen.Value = entry.LastSeenUtc.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    // === Init + helpers ====================================================

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = Mostlylucid.BotDetection.Data.Schema.SchemaLoader.Load("path_lifecycle");
            await cmd.ExecuteNonQueryAsync(ct);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Test-only synchronous drain. The base class doesn't expose its
    ///     drainer; we replicate the persist path here by snapshotting the
    ///     hot tier and writing one UPSERT for each entry. Production uses
    ///     the channel-driven drainer.
    /// </summary>
    private async Task FlushPendingAsync(CancellationToken ct)
    {
        var snapshot = Snapshot().ToArray();
        if (snapshot.Length == 0) return;
        await EnsureInitializedAsync(ct);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = (SqliteTransaction)tx;
            cmd.CommandText = """
                INSERT INTO path_lifecycle
                    (path, first_seen_utc, total_2xx, total_4xx, total_other,
                     last_2xx_utc, first_4xx_after_2xx_utc, last_seen_utc)
                VALUES
                    (@path, @first_seen, @t2, @t4, @to, @last2, @first4, @last_seen)
                ON CONFLICT(path) DO UPDATE SET
                    total_2xx = excluded.total_2xx,
                    total_4xx = excluded.total_4xx,
                    total_other = excluded.total_other,
                    last_2xx_utc = excluded.last_2xx_utc,
                    first_4xx_after_2xx_utc = COALESCE(path_lifecycle.first_4xx_after_2xx_utc, excluded.first_4xx_after_2xx_utc),
                    last_seen_utc = excluded.last_seen_utc
                """;
            foreach (var (path, entry) in snapshot)
            {
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@path", entry.Path);
                cmd.Parameters.AddWithValue("@first_seen", entry.FirstSeenUtc.ToString("O"));
                cmd.Parameters.AddWithValue("@t2", entry.Total2xx);
                cmd.Parameters.AddWithValue("@t4", entry.Total4xx);
                cmd.Parameters.AddWithValue("@to", entry.TotalOther);
                cmd.Parameters.AddWithValue("@last2", (object?)entry.Last2xxUtc?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@first4", (object?)entry.First4xxAfter2xxUtc?.ToString("O") ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@last_seen", entry.LastSeenUtc.ToString("O"));
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();
}

/// <summary>
///     Tuning knobs for <see cref="SqlitePathLifecycleStore"/>. Exposed via
///     <c>BotDetectionOptions.PathLifecycle</c> so users bind through the
///     normal <c>BotDetection:PathLifecycle:*</c> section.
/// </summary>
public sealed class PathLifecycleOptions
{
    /// <summary>Hot-tier LFU cap. Once exceeded by ~10%, coldest entries
    ///     (by last-seen) are evicted. Default: 50,000.</summary>
    public int MaxEntries { get; set; } = 50_000;

    /// <summary>Bounded channel capacity. When full, the oldest queued op
    ///     is dropped to keep Record() non-blocking. Default: 100,000.</summary>
    public int WriteQueueCapacity { get; set; } = 100_000;

    /// <summary>Max ops folded into one SQLite transaction. Larger = fewer
    ///     round-trips, higher per-batch latency. Default: 256.</summary>
    public int BatchMaxSize { get; set; } = 256;

    /// <summary>Sleep between batches. Smaller = lower persistence latency,
    ///     more I/O. Default: 250ms.</summary>
    public TimeSpan DrainInterval { get; set; } = TimeSpan.FromMilliseconds(250);
}
