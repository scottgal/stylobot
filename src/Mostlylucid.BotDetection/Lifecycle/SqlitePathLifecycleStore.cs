using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     SQLite-backed <see cref="IPathLifecycleStore"/>. One row per path,
///     UPSERT on flush. Bounded by an LRU cache of the hottest paths; cold
///     paths still persist on disk and are reloaded on lookup.
/// </summary>
/// <remarks>
///     <para>
///         The hot path is <strong>entirely in-memory</strong>: a
///         <see cref="ConcurrentDictionary"/> holds the live counters, a
///         <see cref="ConcurrentDictionary"/> tracks which paths have been
///         touched since last flush. <see cref="RecordResponseAsync"/> does no
///         I/O. <see cref="PathLifecycleFlushService"/> drains the dirty set
///         on a 30-second timer and writes everything through the persistent
///         connection in a single transaction.
///     </para>
///     <para>
///         Static asset paths (CSS/JS/images/fonts) are filtered at the
///         caller -- they would dominate the table without adding signal.
///     </para>
/// </remarks>
public sealed class SqlitePathLifecycleStore : IPathLifecycleStore, IDisposable
{
    private const int CacheMaxEntries = 50_000;

    private readonly string _connectionString;
    private readonly ILogger<SqlitePathLifecycleStore> _logger;
    private readonly ConcurrentDictionary<string, PathLifecycle> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;
    private SqliteConnection? _persistentConnection;

    public SqlitePathLifecycleStore(string connectionString, ILogger<SqlitePathLifecycleStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            _persistentConnection = new SqliteConnection(_connectionString);
            await _persistentConnection.OpenAsync(ct);

            await using var cmd = _persistentConnection.CreateCommand();
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
    ///     Hot-path write: in-memory only. The path is marked dirty for the
    ///     next flush cycle. No SQLite open, no NTFS handle, no kernel
    ///     transition. <paramref name="ct"/> is intentionally ignored -- the
    ///     work is bounded local memory ops.
    /// </summary>
    public Task RecordResponseAsync(string path, int statusCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return Task.CompletedTask;

        var now = DateTime.UtcNow;
        var is2xx = statusCode is >= 200 and < 300;
        var is4xx = statusCode is >= 400 and < 500;

        _cache.AddOrUpdate(
            path,
            _ => new PathLifecycle
            {
                Path = path,
                FirstSeenUtc = now,
                LastSeenUtc = now,
                Total2xx = is2xx ? 1 : 0,
                Total4xx = is4xx ? 1 : 0,
                TotalOther = (!is2xx && !is4xx) ? 1 : 0,
                Last2xxUtc = is2xx ? now : null,
                First4xxAfter2xxUtc = null
            },
            (_, prev) => prev with
            {
                LastSeenUtc = now,
                Total2xx = prev.Total2xx + (is2xx ? 1 : 0),
                Total4xx = prev.Total4xx + (is4xx ? 1 : 0),
                TotalOther = prev.TotalOther + ((!is2xx && !is4xx) ? 1 : 0),
                Last2xxUtc = is2xx ? now : prev.Last2xxUtc,
                // Lock in the first 4xx that lands AFTER any 2xx history.
                First4xxAfter2xxUtc =
                    is4xx && prev.Total2xx > 0 && !prev.First4xxAfter2xxUtc.HasValue
                        ? now
                        : prev.First4xxAfter2xxUtc
            });

        _dirty[path] = 1;
        if (_cache.Count > CacheMaxEntries) EvictColdest();

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Drain the dirty set and UPSERT each touched path through the
    ///     persistent connection in a single transaction. Called by
    ///     <see cref="PathLifecycleFlushService"/> on a timer and on shutdown.
    ///     A flush failure is logged and the paths stay dirty for the next
    ///     pass -- writes are idempotent UPSERTs so retry is safe.
    /// </summary>
    public async Task FlushAsync(CancellationToken ct = default)
    {
        if (_dirty.IsEmpty) return;
        await EnsureInitializedAsync(ct);
        if (_persistentConnection is null) return;

        // Snapshot the dirty keys; new dirties added during flush stay for the
        // next pass. Clearing-as-we-go would lose updates that arrive mid-flush.
        var keys = _dirty.Keys.ToArray();
        if (keys.Length == 0) return;

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var tx = await _persistentConnection.BeginTransactionAsync(ct);
            await using var cmd = _persistentConnection.CreateCommand();
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

            var written = 0;
            foreach (var key in keys)
            {
                if (!_cache.TryGetValue(key, out var entry)) continue;
                pPath.Value = entry.Path;
                pFirst.Value = entry.FirstSeenUtc.ToString("O");
                pT2.Value = entry.Total2xx;
                pT4.Value = entry.Total4xx;
                pTo.Value = entry.TotalOther;
                pLast2.Value = (object?)entry.Last2xxUtc?.ToString("O") ?? DBNull.Value;
                pFirst4.Value = (object?)entry.First4xxAfter2xxUtc?.ToString("O") ?? DBNull.Value;
                pLastSeen.Value = entry.LastSeenUtc.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
                written++;
            }

            await tx.CommitAsync(ct);

            // Only clear keys we actually wrote -- if the flush was cancelled
            // mid-batch we want the survivors to be retried next pass.
            foreach (var key in keys) _dirty.TryRemove(key, out _);

            if (written > 0)
                _logger.LogDebug("PathLifecycle flushed {Count} dirty paths", written);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PathLifecycle flush failed; paths remain dirty for retry");
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<PathLifecycle?> GetAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return null;

        // Hot path: cache hit.
        if (_cache.TryGetValue(path, out var cached)) return cached;

        await EnsureInitializedAsync(ct);

        // Cold path: try SQLite.
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

            var lifecycle = new PathLifecycle
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
            _cache[path] = lifecycle;
            return lifecycle;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PathLifecycle read failed for {Path}", path);
            return null;
        }
    }

    private void EvictColdest()
    {
        // Drop ~10% to amortise the eviction cost. Coldest = least-recently
        // updated. Not perfect LRU but good enough for a path table.
        var target = CacheMaxEntries - (CacheMaxEntries / 10);
        if (_cache.Count <= target) return;

        var coldest = _cache.OrderBy(kv => kv.Value.LastSeenUtc)
            .Take(_cache.Count - target)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var k in coldest) _cache.TryRemove(k, out _);
    }

    private static DateTime ParseUtc(string s) =>
        DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind).ToUniversalTime();

    public void Dispose()
    {
        _persistentConnection?.Dispose();
        _initLock.Dispose();
        _writeLock.Dispose();
    }
}
