using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data.Contracts;

namespace Mostlylucid.BotDetection.Data;

public sealed class SqliteIntentCentroidStore : IIntentCentroidStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteIntentCentroidStore> _logger;

    public SqliteIntentCentroidStore(string connectionString, ILogger<SqliteIntentCentroidStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>
    /// Upserts an intent centroid using a caller-owned open connection.
    /// The caller owns the connection lifetime; this method does not open or close it.
    /// Exceptions propagate to the caller (no swallowing).
    /// </summary>
    public async Task UpsertIntentAsync(SqliteConnection conn, string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO intent_centroids (signature_id, vector, threat_score, intent_category, updated_at)
            VALUES (@sig, @vec, @ts_score, @cat, @ts)
            ON CONFLICT(signature_id) DO UPDATE SET
                vector=excluded.vector, threat_score=excluded.threat_score,
                intent_category=excluded.intent_category, updated_at=excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@sig", signatureId);
        cmd.Parameters.AddWithValue("@vec", CentroidFloatPacker.Pack(vector));
        cmd.Parameters.AddWithValue("@ts_score", threatScore);
        cmd.Parameters.AddWithValue("@cat", intentCategory);
        cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore, string intentCategory, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await UpsertIntentAsync(conn, signatureId, vector, threatScore, intentCategory, ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertIntent failed for {Sig}", signatureId); }
    }

    public async Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(int limit, CancellationToken ct = default)
    {
        var result = new List<IntentCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, threat_score, intent_category
                FROM intent_centroids ORDER BY updated_at DESC LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new IntentCentroidRow(
                    reader.GetString(0),
                    CentroidFloatPacker.Unpack((byte[])reader[1]),
                    reader.GetDouble(2),
                    reader.GetString(3)));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentIntents failed"); }
        return result;
    }

    public async Task PruneIntentsOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM intent_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoffEpochSeconds);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} intent centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneIntents failed"); }
    }
}
