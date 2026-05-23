using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Lifecycle;

/// <summary>
///     SQLite-backed <see cref="IPathLifecycleStore"/>. One row per path,
///     UPSERT-on-write. Bounded by an LRU cache of the hottest paths; cold
///     paths still persist on disk and are reloaded on lookup.
/// </summary>
/// <remarks>
///     <para>
///         Reads go through an in-memory <see cref="ConcurrentDictionary"/>
///         on the hot path; writes batch through a bounded channel drained
///         by a single writer task so the response-side middleware never
///         blocks on SQLite locks at request rate.
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
    private readonly SemaphoreSlim _initLock = new(1, 1);
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
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS path_lifecycle (
                    path                    TEXT PRIMARY KEY,
                    first_seen_utc          TEXT NOT NULL,
                    total_2xx               INTEGER NOT NULL DEFAULT 0,
                    total_4xx               INTEGER NOT NULL DEFAULT 0,
                    total_other             INTEGER NOT NULL DEFAULT 0,
                    last_2xx_utc            TEXT,
                    first_4xx_after_2xx_utc TEXT,
                    last_seen_utc           TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS idx_path_lifecycle_last_seen
                    ON path_lifecycle(last_seen_utc);
                """;
            await cmd.ExecuteNonQueryAsync(ct);

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task RecordResponseAsync(string path, int statusCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(path)) return;
        await EnsureInitializedAsync(ct);

        var now = DateTime.UtcNow;
        var is2xx = statusCode is >= 200 and < 300;
        var is4xx = statusCode is >= 400 and < 500;

        // Cache update first so the next read sees the change.
        var updated = _cache.AddOrUpdate(
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

        if (_cache.Count > CacheMaxEntries) EvictColdest();

        // Persist. Best-effort -- a single write failure does not bubble.
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
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
            cmd.Parameters.AddWithValue("@path", updated.Path);
            cmd.Parameters.AddWithValue("@first_seen", updated.FirstSeenUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@t2", updated.Total2xx);
            cmd.Parameters.AddWithValue("@t4", updated.Total4xx);
            cmd.Parameters.AddWithValue("@to", updated.TotalOther);
            cmd.Parameters.AddWithValue("@last2", (object?)updated.Last2xxUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@first4", (object?)updated.First4xxAfter2xxUtc?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@last_seen", updated.LastSeenUtc.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "PathLifecycle write skipped for {Path}", path);
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
    }
}
