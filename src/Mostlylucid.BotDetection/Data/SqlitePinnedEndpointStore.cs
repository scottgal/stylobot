using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data;

public sealed class SqlitePinnedEndpointStore : IPinnedEndpointStore
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _existingConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public SqlitePinnedEndpointStore(IOptions<BotDetectionOptions> options)
    {
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        StoreDbDirectory.EnsureExists(basePath);
        _connectionString = $"Data Source={Path.Combine(basePath, "sessions.db")};Cache=Shared";
        InitSchema();
    }

    internal SqlitePinnedEndpointStore(SqliteConnection existingConnection)
    {
        _connectionString = existingConnection.ConnectionString;
        _existingConnection = existingConnection;
        InitSchema();
    }

    private void InitSchema()
    {
        var (conn, owned) = GetConnection();
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = Schema.SchemaLoader.Load("pinned_endpoints");
            cmd.ExecuteNonQuery();
        }
        finally { if (owned) conn.Dispose(); }
    }

    public async Task<IReadOnlyList<PinnedEndpoint>> GetAllAsync(CancellationToken ct = default)
    {
        var (conn, owned) = await GetConnectionAsync(ct);
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT id, method, path, is_honeypot, note, created_at
                FROM pinned_endpoints
                ORDER BY created_at DESC
                """;
            var results = new List<PinnedEndpoint>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(ReadPin(reader));
            return results;
        }
        finally { if (owned) await conn.DisposeAsync(); }
    }

    public async Task<PinnedEndpoint?> AddAsync(
        string method, string path, bool isHoneypot, string? note,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var (conn, owned) = await GetConnectionAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO pinned_endpoints (method, path, is_honeypot, note, created_at)
                    VALUES (@method, @path, @hon, @note, @at)
                    ON CONFLICT (method, path) DO NOTHING;

                    SELECT id, method, path, is_honeypot, note, created_at
                    FROM pinned_endpoints
                    WHERE method = @method AND path = @path;
                    """;
                cmd.Parameters.AddWithValue("@method", method);
                cmd.Parameters.AddWithValue("@path", path);
                cmd.Parameters.AddWithValue("@hon", isHoneypot ? 1 : 0);
                cmd.Parameters.AddWithValue("@note", note ?? (object)DBNull.Value);
                cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());

                await using var reader = await cmd.ExecuteReaderAsync(ct);
                if (await reader.ReadAsync(ct))
                    return ReadPin(reader);
                return null;
            }
            finally { if (owned) await conn.DisposeAsync(); }
        }
        finally { _writeLock.Release(); }
    }

    public async Task<bool> RemoveAsync(long id, CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var (conn, owned) = await GetConnectionAsync(ct);
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = "DELETE FROM pinned_endpoints WHERE id = @id";
                cmd.Parameters.AddWithValue("@id", id);
                var rows = await cmd.ExecuteNonQueryAsync(ct);
                return rows > 0;
            }
            finally { if (owned) await conn.DisposeAsync(); }
        }
        finally { _writeLock.Release(); }
    }

    private static PinnedEndpoint ReadPin(SqliteDataReader r) =>
        new(r.GetInt64(0), r.GetString(1), r.GetString(2),
            r.GetInt32(3) != 0, r.IsDBNull(4) ? null : r.GetString(4),
            DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(5)));

    private (SqliteConnection conn, bool owned) GetConnection()
    {
        if (_existingConnection != null) return (_existingConnection, false);
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return (conn, true);
    }

    private async Task<(SqliteConnection conn, bool owned)> GetConnectionAsync(CancellationToken ct)
    {
        if (_existingConnection != null) return (_existingConnection, false);
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return (conn, true);
    }
}
