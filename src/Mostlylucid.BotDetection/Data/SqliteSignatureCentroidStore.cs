using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Data.Contracts;

namespace Mostlylucid.BotDetection.Data;

public sealed class SqliteSignatureCentroidStore : ISignatureCentroidStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteSignatureCentroidStore> _logger;

    public SqliteSignatureCentroidStore(string connectionString, ILogger<SqliteSignatureCentroidStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO signature_centroids (signature_id, vector, was_bot, confidence, access_count, updated_at)
                VALUES (@sig, @vec, @bot, @conf, 1, @ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, was_bot=excluded.was_bot,
                    confidence=excluded.confidence,
                    access_count=signature_centroids.access_count + 1,
                    updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@sig", signatureId);
            cmd.Parameters.AddWithValue("@vec", CentroidFloatPacker.Pack(vector));
            cmd.Parameters.AddWithValue("@bot", wasBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@conf", confidence);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertSignature failed for {Sig}", signatureId); }
    }

    public async Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(int limit, CancellationToken ct = default)
    {
        var result = new List<SignatureCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT signature_id, vector, was_bot, confidence, access_count
                FROM signature_centroids ORDER BY updated_at DESC LIMIT @lim
                """;
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new SignatureCentroidRow(
                    reader.GetString(0),
                    CentroidFloatPacker.Unpack((byte[])reader[1]),
                    reader.GetInt32(2) != 0,
                    reader.GetDouble(3),
                    reader.GetInt32(4)));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentSignatures failed"); }
        return result;
    }

    public async Task PruneSignaturesOlderThanAsync(long cutoffEpochSeconds, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM signature_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoffEpochSeconds);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} signature centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneSignatures failed"); }
    }
}
