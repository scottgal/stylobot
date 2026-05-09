// File: src/Mostlylucid.BotDetection/Data/SqliteVectorCentroidStore.cs
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     Persistent store for compressed vector centroids (L1/L2 from VectorCompactionService).
///     Replaces the HNSW JSON files. Three tables: signature_centroids, session_centroids, intent_centroids.
///     All writes are fire-and-forget; swallows exceptions and logs warnings to keep the fast path safe.
/// </summary>
public sealed class SqliteVectorCentroidStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteVectorCentroidStore> _logger;

    public SqliteVectorCentroidStore(string connectionString, ILogger<SqliteVectorCentroidStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    // ── Signature centroids ──────────────────────────────────────────────────

    public async Task UpsertSignatureAsync(string signatureId, float[] vector, bool wasBot, double confidence,
        CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO signature_centroids (signature_id, vector, was_bot, confidence, updated_at)
                VALUES (@sig, @vec, @bot, @conf, @ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, was_bot=excluded.was_bot,
                    confidence=excluded.confidence, updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@sig", signatureId);
            cmd.Parameters.AddWithValue("@vec", PackFloats(vector));
            cmd.Parameters.AddWithValue("@bot", wasBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@conf", confidence);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertSignature failed for {Sig}", signatureId); }
    }

    public async Task<IReadOnlyList<SignatureCentroidRow>> GetRecentSignaturesAsync(
        int limit, CancellationToken ct = default)
    {
        var result = new List<SignatureCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT signature_id, vector, was_bot, confidence FROM signature_centroids ORDER BY updated_at DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new SignatureCentroidRow(
                    reader.GetString(0),
                    UnpackFloats((byte[])reader[1]),
                    reader.GetInt32(2) != 0,
                    reader.GetDouble(3)));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentSignatures failed"); }
        return result;
    }

    public async Task PruneSignaturesOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM signature_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToUnixTimeSeconds());
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} signature centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneSignatures failed"); }
    }

    // ── Session centroids ────────────────────────────────────────────────────

    public async Task UpsertSessionAsync(SessionCentroidRow row, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
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
            cmd.Parameters.AddWithValue("@vec", PackFloats(row.Vector));
            cmd.Parameters.AddWithValue("@vel", row.VelocityVector != null ? PackFloats(row.VelocityVector) : DBNull.Value);
            cmd.Parameters.AddWithValue("@var", row.VarianceVector != null ? PackFloats(row.VarianceVector) : DBNull.Value);
            cmd.Parameters.AddWithValue("@freq", row.FreqFingerprint != null ? PackFloats(row.FreqFingerprint) : DBNull.Value);
            cmd.Parameters.AddWithValue("@cid", (object?)row.ClusterId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@lvl", row.CompressionLevel);
            cmd.Parameters.AddWithValue("@bot", row.IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", row.BotProbability);
            cmd.Parameters.AddWithValue("@pri", row.Priority);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertSession failed for {Sig}", row.SignatureId); }
    }

    public async Task<IReadOnlyList<SessionCentroidRow>> GetRecentSessionsAsync(
        int limit, CancellationToken ct = default)
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
                    Vector           = UnpackFloats((byte[])reader[1]),
                    VelocityVector   = reader.IsDBNull(2) ? null : UnpackFloats((byte[])reader[2]),
                    VarianceVector   = reader.IsDBNull(3) ? null : UnpackFloats((byte[])reader[3]),
                    FreqFingerprint  = reader.IsDBNull(4) ? null : UnpackFloats((byte[])reader[4]),
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

    public async Task PruneSessionsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM session_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToUnixTimeSeconds());
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} session centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneSessionCentroids failed"); }
    }

    // ── Intent centroids ─────────────────────────────────────────────────────

    public async Task UpsertIntentAsync(string signatureId, float[] vector, double threatScore,
        string intentCategory, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO intent_centroids (signature_id, vector, threat_score, intent_category, updated_at)
                VALUES (@sig, @vec, @ts_score, @cat, @ts)
                ON CONFLICT(signature_id) DO UPDATE SET
                    vector=excluded.vector, threat_score=excluded.threat_score,
                    intent_category=excluded.intent_category, updated_at=excluded.updated_at;
                """;
            cmd.Parameters.AddWithValue("@sig", signatureId);
            cmd.Parameters.AddWithValue("@vec", PackFloats(vector));
            cmd.Parameters.AddWithValue("@ts_score", threatScore);
            cmd.Parameters.AddWithValue("@cat", intentCategory);
            cmd.Parameters.AddWithValue("@ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            await cmd.ExecuteNonQueryAsync(ct);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "UpsertIntent failed for {Sig}", signatureId); }
    }

    public async Task<IReadOnlyList<IntentCentroidRow>> GetRecentIntentsAsync(
        int limit, CancellationToken ct = default)
    {
        var result = new List<IntentCentroidRow>(limit);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT signature_id, vector, threat_score, intent_category FROM intent_centroids ORDER BY updated_at DESC LIMIT @lim";
            cmd.Parameters.AddWithValue("@lim", limit);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
                result.Add(new IntentCentroidRow(
                    reader.GetString(0),
                    UnpackFloats((byte[])reader[1]),
                    reader.GetDouble(2),
                    reader.GetString(3)));
        }
        catch (Exception ex) { _logger.LogWarning(ex, "GetRecentIntents failed"); }
        return result;
    }

    public async Task PruneIntentsOlderThanAsync(DateTimeOffset cutoff, CancellationToken ct = default)
    {
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM intent_centroids WHERE updated_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToUnixTimeSeconds());
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0) _logger.LogDebug("Pruned {Count} intent centroids", deleted);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "PruneIntents failed"); }
    }

    // ── Float packing ────────────────────────────────────────────────────────

    internal static byte[] PackFloats(float[] v) =>
        MemoryMarshal.AsBytes(v.AsSpan()).ToArray();

    internal static float[] UnpackFloats(byte[] b)
    {
        var result = new float[b.Length / sizeof(float)];
        MemoryMarshal.Cast<byte, float>(b).CopyTo(result);
        return result;
    }
}

// ── Row types ────────────────────────────────────────────────────────────────

public sealed record SignatureCentroidRow(
    string SignatureId, float[] Vector, bool WasBot, double Confidence);

public sealed class SessionCentroidRow
{
    public string SignatureId       { get; init; } = "";
    public float[] Vector           { get; init; } = [];
    public float[]? VelocityVector  { get; init; }
    public float[]? VarianceVector  { get; init; }
    public float[]? FreqFingerprint { get; init; }
    public string? ClusterId        { get; init; }
    public int CompressionLevel     { get; init; }
    public bool IsBot               { get; init; }
    public double BotProbability    { get; init; }
    public double Priority          { get; init; }
}

public sealed record IntentCentroidRow(
    string SignatureId, float[] Vector, double ThreatScore, string IntentCategory);
