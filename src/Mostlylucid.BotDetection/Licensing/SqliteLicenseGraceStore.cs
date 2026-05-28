using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Licensing;

/// <summary>
///     Persists grace period start time to SQLite so it survives restarts.
///     Table: license_state (id=1, grace_started_at INTEGER ms, updated_at INTEGER ms)
/// </summary>
internal sealed class SqliteLicenseGraceStore : ILicenseGraceStore
{
    private readonly string _connectionString;
    private readonly string _dbPath;

    public SqliteLicenseGraceStore(IOptions<BotDetectionOptions> options)
    {
        _dbPath = options.Value.DatabasePath
                  ?? System.IO.Path.Combine(AppContext.BaseDirectory, "botdetection.db");
        _connectionString = $"Data Source={_dbPath}";
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(_dbPath));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS license_state (
                id               INTEGER PRIMARY KEY DEFAULT 1,
                grace_started_at INTEGER,
                updated_at       INTEGER NOT NULL
            );
            INSERT OR IGNORE INTO license_state (id, updated_at) VALUES (1, 0);
            """;
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<DateTimeOffset?> GetGraceStartedAtAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT grace_started_at FROM license_state WHERE id = 1;";
        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is long ms and > 0) return DateTimeOffset.FromUnixTimeMilliseconds(ms);
        return null;
    }

    public async Task SetGraceStartedAtAsync(DateTimeOffset value, CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO license_state (id, grace_started_at, updated_at)
            VALUES (1, $grace, $now)
            ON CONFLICT(id) DO UPDATE SET grace_started_at = $grace, updated_at = $now;
            """;
        cmd.Parameters.AddWithValue("$grace", value.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task ClearGraceStartedAtAsync(CancellationToken ct = default)
    {
        using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE license_state SET grace_started_at = NULL, updated_at = $now WHERE id = 1;
            """;
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
