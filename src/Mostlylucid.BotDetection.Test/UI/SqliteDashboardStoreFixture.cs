using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.Test.UI;

/// <summary>
///     Shared test fixture: creates a fresh temp-dir SQLite store per test and deletes it on dispose.
///     Use <see cref="NewAsync"/> to create, await using in the test body.
/// </summary>
internal sealed class SqliteDashboardStoreFixture : IAsyncDisposable
{
    public required string TempDir { get; init; }
    public required SqliteDashboardEventStore Store { get; init; }

    public static async Task<SqliteDashboardStoreFixture> NewAsync(string? tag = null)
    {
        var name = tag is null
            ? $"stylobot-fx-{Guid.NewGuid():N}"
            : $"stylobot-fx-{tag}-{Guid.NewGuid():N}";
        var tempDir = Path.Combine(Path.GetTempPath(), name);
        Directory.CreateDirectory(tempDir);
        var fakeDbPath = Path.Combine(tempDir, "botdetection.db");

        var opts  = Options.Create(new BotDetectionOptions { DatabasePath = fakeDbPath });
        var store = new SqliteDashboardEventStore(NullLogger<SqliteDashboardEventStore>.Instance, opts);

        // Trigger schema init via a read-touching call (same convention as Task 1–4 tests).
        _ = await store.GetDetectionsAsync();

        // GetTopBotsAsync uses a correlated subquery against the 'sessions' table, which is
        // normally created by SqliteSessionStore (core package) in the same DB file.
        // Create a stub table so the query does not throw in the isolated test DB.
        await EnsureSessionsTableAsync(DashboardDbPath.GetConnectionString(opts.Value));

        return new SqliteDashboardStoreFixture { TempDir = tempDir, Store = store };
    }

    /// <summary>
    ///     Creates a minimal stub of the sessions table expected by <c>GetTopBotsAsync</c>'s
    ///     correlated subquery. In production the table is owned by SqliteSessionStore; here
    ///     we only need it to exist so the SQL parser succeeds. No rows are inserted.
    /// </summary>
    private static async Task EnsureSessionsTableAsync(string connectionString)
    {
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                signature TEXT NOT NULL,
                paths_json TEXT,
                is_bot INTEGER NOT NULL DEFAULT 0,
                ended_at TEXT
            )
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        try { if (Directory.Exists(TempDir)) Directory.Delete(TempDir, recursive: true); } catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }
}
