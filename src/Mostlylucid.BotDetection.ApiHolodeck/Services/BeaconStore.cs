using Microsoft.Data.Sqlite;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.ApiHolodeck.Services;

/// <summary>
///     Stores beacon canary to fingerprint mappings in SQLite.
///     Used to correlate rotated fingerprints when they replay canary values.
/// </summary>
public sealed class BeaconStore : IBeaconStore, IDisposable
{
    private readonly string _connectionString;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public BeaconStore(string connectionString)
    {
        _connectionString = connectionString;
    }

    public record BeaconRecord(string Fingerprint, string Path, string? PackId, DateTime CreatedAt);

    private async Task EnsureInitializedAsync()
    {
        if (_initialized) return;
        await _initLock.WaitAsync();
        try
        {
            if (_initialized) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS beacons (
                    canary TEXT PRIMARY KEY,
                    fingerprint TEXT NOT NULL,
                    path TEXT NOT NULL,
                    pack_id TEXT,
                    created_at TEXT NOT NULL,
                    expires_at TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_beacons_expires ON beacons(expires_at);
                CREATE INDEX IF NOT EXISTS ix_beacons_fingerprint ON beacons(fingerprint);
                """;
            await cmd.ExecuteNonQueryAsync();
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task StoreAsync(string canary, string fingerprint, string path, string? packId, TimeSpan ttl)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO beacons (canary, fingerprint, path, pack_id, created_at, expires_at)
            VALUES (@canary, @fingerprint, @path, @packId, @createdAt, @expiresAt)
            """;
        var now = DateTime.UtcNow;
        cmd.Parameters.AddWithValue("@canary", canary);
        cmd.Parameters.AddWithValue("@fingerprint", fingerprint);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@packId", (object?)packId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@createdAt", now.ToString("O"));
        cmd.Parameters.AddWithValue("@expiresAt", (now + ttl).ToString("O"));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<BeaconRecord?> LookupAsync(string canary)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint, path, pack_id, created_at FROM beacons
            WHERE canary = @canary AND expires_at > @now
            """;
        cmd.Parameters.AddWithValue("@canary", canary);
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) return null;
        return new BeaconRecord(
            reader.GetString(0),
            reader.GetString(1),
            reader.IsDBNull(2) ? null : reader.GetString(2),
            DateTime.Parse(reader.GetString(3)));
    }

    public async Task<Dictionary<string, BeaconRecord>> BatchLookupAsync(IReadOnlyList<string> canaries)
    {
        if (canaries.Count == 0) return new();
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var results = new Dictionary<string, BeaconRecord>(StringComparer.OrdinalIgnoreCase);
        var now = DateTime.UtcNow.ToString("O");

        foreach (var batch in canaries.Chunk(50))
        {
            await using var cmd = conn.CreateCommand();
            var paramNames = new List<string>();
            for (var i = 0; i < batch.Length; i++)
            {
                var pname = $"@c{i}";
                paramNames.Add(pname);
                cmd.Parameters.AddWithValue(pname, batch[i]);
            }
            cmd.Parameters.AddWithValue("@now", now);
            cmd.CommandText = $"""
                SELECT canary, fingerprint, path, pack_id, created_at FROM beacons
                WHERE canary IN ({string.Join(",", paramNames)}) AND expires_at > @now
                """;
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                results[reader.GetString(0)] = new BeaconRecord(
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.IsDBNull(3) ? null : reader.GetString(3),
                    DateTime.Parse(reader.GetString(4)));
            }
        }

        return results;
    }

    public async Task<int> CleanupExpiredAsync()
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM beacons WHERE expires_at <= @now";
        cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
        return await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose()
    {
        _initLock.Dispose();
    }
}
