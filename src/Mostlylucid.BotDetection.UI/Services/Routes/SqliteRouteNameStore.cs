using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.UI.Services.Routes;

public sealed class SqliteRouteNameStore : IRouteNameStore
{
    private readonly SqliteConnection? _sharedConn;
    private readonly string? _connectionString;
    private readonly ILogger<SqliteRouteNameStore> _logger;

    public SqliteRouteNameStore(SqliteConnection conn, ILogger<SqliteRouteNameStore> logger)
    {
        _sharedConn = conn;
        _logger = logger;
    }

    public SqliteRouteNameStore(string connectionString, ILogger<SqliteRouteNameStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            const string schemaSql = """
                CREATE TABLE IF NOT EXISTS route_names (
                    route_key     TEXT PRIMARY KEY,
                    friendly_name TEXT NOT NULL,
                    notes         TEXT,
                    updated_utc   TEXT NOT NULL,
                    updated_by    TEXT
                );
                """;
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = schemaSql;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (shouldDispose) await conn.DisposeAsync();
        }
    }

    public async Task<RouteNameEntry?> GetAsync(string routeKey, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT route_key, friendly_name, notes, updated_utc, updated_by
                FROM route_names WHERE route_key = @k
                """;
            cmd.Parameters.AddWithValue("@k", routeKey);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            return await reader.ReadAsync(ct) ? ReadRow(reader) : null;
        }
        finally
        {
            if (shouldDispose) await conn.DisposeAsync();
        }
    }

    public async Task<IReadOnlyList<RouteNameEntry>> GetAllAsync(CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT route_key, friendly_name, notes, updated_utc, updated_by
                FROM route_names ORDER BY route_key ASC
                """;
            var results = new List<RouteNameEntry>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct)) results.Add(ReadRow(reader));
            return results;
        }
        finally
        {
            if (shouldDispose) await conn.DisposeAsync();
        }
    }

    public async Task SetAsync(string routeKey, string friendlyName, string? notes, string? updatedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(friendlyName))
            throw new ArgumentException("Friendly name must be non-empty.", nameof(friendlyName));
        if (string.IsNullOrWhiteSpace(routeKey))
            throw new ArgumentException("Route key must be non-empty.", nameof(routeKey));

        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO route_names (route_key, friendly_name, notes, updated_utc, updated_by)
                VALUES (@k, @n, @notes, @t, @by)
                ON CONFLICT(route_key) DO UPDATE SET
                    friendly_name = excluded.friendly_name,
                    notes         = excluded.notes,
                    updated_utc   = excluded.updated_utc,
                    updated_by    = excluded.updated_by
                """;
            cmd.Parameters.AddWithValue("@k", routeKey);
            cmd.Parameters.AddWithValue("@n", friendlyName);
            cmd.Parameters.AddWithValue("@notes", (object?)notes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@t", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@by", (object?)updatedBy ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (shouldDispose) await conn.DisposeAsync();
        }
    }

    public async Task RemoveAsync(string routeKey, CancellationToken ct = default)
    {
        var conn = await GetConnectionAsync(ct);
        var shouldDispose = _sharedConn == null;
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM route_names WHERE route_key = @k";
            cmd.Parameters.AddWithValue("@k", routeKey);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            if (shouldDispose) await conn.DisposeAsync();
        }
    }

    private static RouteNameEntry ReadRow(SqliteDataReader r) => new(
        RouteKey: r.GetString(0),
        FriendlyName: r.GetString(1),
        Notes: r.IsDBNull(2) ? null : r.GetString(2),
        UpdatedUtc: DateTime.Parse(r.GetString(3), null, DateTimeStyles.RoundtripKind),
        UpdatedBy: r.IsDBNull(4) ? null : r.GetString(4));

    private async Task<SqliteConnection> GetConnectionAsync(CancellationToken ct)
    {
        if (_sharedConn != null) return _sharedConn;
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        return conn;
    }
}
