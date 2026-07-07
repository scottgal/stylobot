using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data.Contracts;

namespace Mostlylucid.BotDetection.Data;

public sealed class SqliteSessionCentroidStore : ISessionCentroidStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSessionCentroidStore> _logger;

    public SqliteSessionCentroidStore(string connectionString, ILogger<SqliteSessionCentroidStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>
    /// Upserts a session centroid using a caller-owned open connection.
    /// The caller owns the connection lifetime; this method does not open or close it.
    /// Exceptions propagate to the caller (no swallowing).
    /// </summary>
    public async Task UpsertSessionAsync(SqliteConnection conn, SessionCentroidRow row, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO session_centroids
                (signature_id, vector, velocity_vector, variance_vector, freq_fingerprint,
                 cluster_id, compression_level, is_bot, bot_probability, priority, updated_at)
            VALUES (@sig,@vec,@vel,@var,@freq,@cid,@lvl,@bot,@prob,@pri,@ts)
            ON CONFLICT(signature_id) DO UPDATE SET
                vector=excluded.vector, velocity_vector=excluded.velocity_vector,
                variance_vector=excluded.variance_vector, freq_fingerprint=excluded.freq_fingerprint,
                cluster_id=excluded.cluster_id, compression_level=excluded.compression_level,
                is_bot=excluded.is_bot, bot_probability=excluded.bot_probability,
                priority=excluded.priority, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@sig", row.SignatureId);
        cmd.Parameters.AddWithValue("@vec", CentroidFloatPacker.Pack(row.Vector));
        cmd.Parameters.AddWithValue("@vel", row.VelocityVector != null ? (object)CentroidFloatPacker.Pack(row.VelocityVector) : DBNull.Value);
        cmd.Parameters.AddWithValue("@var", row.VarianceVector != null ? (object)CentroidFloatPacker.Pack(row.VarianceVector) : DBNull.Value);
        cmd.Parameters.AddWithValue("@freq", row.FreqFingerprint != null ? (object)CentroidFloatPacker.Pack(row.FreqFingerprint) : DBNull.Value);
        cmd.Parameters.AddWithValue("@cid", (object?)row.ClusterId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lvl", row.CompressionLevel);
        cmd.Parameters.AddWithValue("@bot", row.IsBot ? 1 : 0);
        cmd.Parameters.AddWithValue("@prob", row.BotProbability);
        cmd.Parameters.AddWithValue("@pri", row.Priority);
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await UpsertSessionAsync(conn, row, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertSession failed for {Sig}", row.SignatureId); }
    }

    public async Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(int limit, CancellationToken ct = default)
    {
        var result = new List<SessionCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, velocity_vector, variance_vector, freq_fingerprint,
                       cluster_id, compression_level, is_bot, bot_probability, priority
                FROM session_centroids ORDER BY updated_at DESC LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new SessionCentroidRow
                {
                    SignatureId      = reader.GetString(0),
                    Vector           = CentroidFloatPacker.Unpack((byte[])reader[1]),
                    VelocityVector   = reader.IsDBNull(2) ? null : CentroidFloatPacker.Unpack((byte[])reader[2]),
                    VarianceVector   = reader.IsDBNull(3) ? null : CentroidFloatPacker.Unpack((byte[])reader[3]),
                    FreqFingerprint  = reader.IsDBNull(4) ? null : CentroidFloatPacker.Unpack((byte[])reader[4]),
                    ClusterId        = reader.IsDBNull(5) ? null : reader.GetString(5),
                    CompressionLevel = reader.GetInt32(6),
                    IsBot            = reader.GetInt32(7) != 0,
                    BotProbability   = reader.GetDouble(8),
                    Priority         = reader.GetDouble(9),
                });
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentSessions failed"); }
        return result;
    }

    public async Task PruneSessionsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM session_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoffEpochSeconds);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} session centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneSessionCentroids failed"); }
    }
}
