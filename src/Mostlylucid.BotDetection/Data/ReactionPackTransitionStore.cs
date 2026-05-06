using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Data;

public sealed record ReactionPackTransition(
    string PackName,
    int FromLevel,
    int ToLevel,
    string TriggeredBy,
    double SignalValue,
    DateTimeOffset OccurredAt);

public sealed class ReactionPackTransitionStore
{
    private readonly string _connectionString;
    private readonly SqliteConnection? _existingConnection;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public ReactionPackTransitionStore(IOptions<BotDetectionOptions> options)
    {
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        _connectionString = $"Data Source={Path.Combine(basePath, "sessions.db")};Cache=Shared";
    }

    internal ReactionPackTransitionStore(SqliteConnection existingConnection)
    {
        _connectionString = existingConnection.ConnectionString;
        _existingConnection = existingConnection;
    }

    public async Task RecordTransitionAsync(
        string packName, int fromLevel, int toLevel,
        string triggeredBy, double signalValue,
        CancellationToken ct = default)
    {
        await _writeLock.WaitAsync(ct);
        try
        {
            var (conn, owned) = GetConnection();
            try
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO reaction_pack_transitions
                        (pack_name, from_level, to_level, triggered_by, signal_value, occurred_at)
                    VALUES (@pack, @from, @to, @by, @val, @at)
                    """;
                cmd.Parameters.AddWithValue("@pack", packName);
                cmd.Parameters.AddWithValue("@from", fromLevel);
                cmd.Parameters.AddWithValue("@to", toLevel);
                cmd.Parameters.AddWithValue("@by", triggeredBy);
                cmd.Parameters.AddWithValue("@val", signalValue);
                cmd.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                await cmd.ExecuteNonQueryAsync(ct);
            }
            finally { if (owned) await conn.DisposeAsync(); }
        }
        finally { _writeLock.Release(); }
    }

    public async Task<IReadOnlyList<ReactionPackTransition>> GetRecentTransitionsAsync(
        string packName, int limit = 50, CancellationToken ct = default)
    {
        var (conn, owned) = GetConnection();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT pack_name, from_level, to_level, triggered_by, signal_value, occurred_at
                FROM reaction_pack_transitions
                WHERE pack_name = @pack
                ORDER BY occurred_at DESC, id DESC
                LIMIT @limit
                """;
            cmd.Parameters.AddWithValue("@pack", packName);
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<ReactionPackTransition>();
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                results.Add(new ReactionPackTransition(
                    reader.GetString(0), reader.GetInt32(1), reader.GetInt32(2),
                    reader.GetString(3), reader.GetDouble(4),
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(5))));
            return results;
        }
        finally { if (owned) await conn.DisposeAsync(); }
    }

    public async Task<int> GetLatestActiveLevelAsync(string packName, CancellationToken ct = default)
    {
        var (conn, owned) = GetConnection();
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT to_level FROM reaction_pack_transitions
                WHERE pack_name = @pack
                ORDER BY occurred_at DESC, id DESC
                LIMIT 1
                """;
            cmd.Parameters.AddWithValue("@pack", packName);
            var result = await cmd.ExecuteScalarAsync(ct);
            return result is long l ? (int)l : 0;
        }
        finally { if (owned) await conn.DisposeAsync(); }
    }

    private (SqliteConnection conn, bool owned) GetConnection()
    {
        if (_existingConnection != null)
            return (_existingConnection, false);
        var conn = new SqliteConnection(_connectionString);
        conn.Open();
        return (conn, true);
    }
}
