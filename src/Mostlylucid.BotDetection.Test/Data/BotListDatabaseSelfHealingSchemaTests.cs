using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Mostlylucid.BotDetection.Data;

namespace Mostlylucid.BotDetection.Test.Data;

/// <summary>
///     Pins the self-healing schema contract on
///     <see cref="BotListDatabase"/>. Surfaced by production noise on
///     Maxo .15: a stale <c>botdetection.db</c> file existed with the
///     <c>bot_patterns</c> + <c>datacenter_ips</c> tables but no
///     <c>list_updates</c> table, and every minute the
///     <c>BotListUpdateService</c> spammed:
///     <code>
///     SqliteException: SQLite Error 1: 'no such table: list_updates'.
///        at BotListDatabase.GetLastUpdateTimeAsync(...)
///     </code>
///     because <c>_initialized</c> was true (Initialize ran once and set
///     the flag) but the schema state on disk diverged. The schema-ensure
///     call on every operation closes that loop: missing table -> recreate
///     -> next read succeeds. Idempotent + cheap (~microseconds for a no-op
///     CREATE TABLE IF NOT EXISTS).
/// </summary>
public class BotListDatabaseSelfHealingSchemaTests : IDisposable
{
    private readonly string _dbPath;
    private readonly DummyFetcher _fetcher = new();

    public BotListDatabaseSelfHealingSchemaTests()
    {
        _dbPath = Path.Combine(Path.GetTempPath(), "stylobot-bld-self-heal-" + Guid.NewGuid().ToString("N") + ".db");
    }

    public void Dispose()
    {
        try { File.Delete(_dbPath); } catch { /* test cleanup */ }
    }

    [Fact]
    public async Task GetLastUpdateTimeAsync_OnDbMissingListUpdatesTable_SelfHeals()
    {
        // Simulate the .15 production state: a pre-existing db with the
        // bot_patterns table but no list_updates table (the schema
        // diverged from a partial init somewhere along its history).
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE bot_patterns (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    name TEXT NOT NULL,
                    pattern TEXT NOT NULL,
                    category TEXT NOT NULL,
                    url TEXT,
                    is_verified INTEGER NOT NULL DEFAULT 0,
                    created_at TEXT NOT NULL,
                    UNIQUE(pattern)
                );
                """;
            await cmd.ExecuteNonQueryAsync();
        }

        var db = new BotListDatabase(
            _fetcher,
            NullLogger<BotListDatabase>.Instance,
            _dbPath);

        // GetLastUpdateTimeAsync must NOT throw "no such table: list_updates"
        // -- the schema ensure call recreates the missing table on the fly.
        // (Initialize then runs an immediate-fetch + records a row, so the
        // return value isn't load-bearing; the load-bearing assertion is
        // simply "the call returned without throwing".)
        _ = await db.GetLastUpdateTimeAsync("bot_patterns");

        // Verify the table now exists on disk.
        await using (var conn = new SqliteConnection($"Data Source={_dbPath}"))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText =
                "SELECT 1 FROM sqlite_master WHERE type='table' AND name='list_updates'";
            var found = await cmd.ExecuteScalarAsync();
            Assert.NotNull(found);
        }
    }

    [Fact]
    public async Task SchemaEnsureIsIdempotent_RepeatedCallsDontFail()
    {
        // Defensive: schema-ensure runs on every operation, so it must be
        // safe to call repeatedly. CREATE TABLE IF NOT EXISTS handles this
        // at the SQL level; this test pins the round-trip.
        var db = new BotListDatabase(
            _fetcher,
            NullLogger<BotListDatabase>.Instance,
            _dbPath);

        await db.GetLastUpdateTimeAsync("bot_patterns");
        await db.GetLastUpdateTimeAsync("bot_patterns");
        await db.GetLastUpdateTimeAsync("datacenter_ips");
        await db.GetLastUpdateTimeAsync("bot_patterns");
        // No assertion -- the test pins "doesn't throw" over a sequence of
        // calls that hit the EnsureSchemaAsync path multiple times.
    }

    private sealed class DummyFetcher : IBotListFetcher
    {
        public Task<List<string>> GetBotPatternsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());
        public Task<List<string>> GetDatacenterIpRangesAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<string>());
        public Task<IReadOnlyDictionary<string, IReadOnlyList<string>>> GetDatacenterIpRangesByVendorAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyDictionary<string, IReadOnlyList<string>>>(new Dictionary<string, IReadOnlyList<string>>());
        public Task<List<BotPattern>> GetMatomoBotPatternsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<BotPattern>());
        public Task<List<SecurityToolPattern>> GetSecurityToolPatternsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new List<SecurityToolPattern>());
        public Task<IReadOnlyList<int>> GetVpnAsnsAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<int>>(Array.Empty<int>());
    }
}
