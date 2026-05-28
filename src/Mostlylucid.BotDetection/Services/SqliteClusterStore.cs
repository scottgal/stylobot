using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     SQLite persistence for <see cref="BotCluster"/>. Owns the <c>clusters</c> table and
///     a single write lock. <see cref="BotClusterService"/> hydrates its in-memory snapshot
///     from <see cref="LoadAllAsync"/> on startup and write-throughs to
///     <see cref="ReplaceAllAsync"/> after each Leiden/LabelPropagation cycle, and to
///     <see cref="UpdateLabelAsync"/> whenever the LLM completes a cluster description.
///
///     The in-memory snapshot stays as a fast lookup cache; this store is the source of
///     truth. Restart restores the full snapshot — including LLM-derived labels and
///     descriptions — instead of starting from an empty <see cref="ClusterSnapshot.Empty"/>.
/// </summary>
public sealed class SqliteClusterStore : IClusterStore
{
    private readonly string _connectionString;
    private readonly ILogger<SqliteClusterStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialised;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    public SqliteClusterStore(string connectionString, ILogger<SqliteClusterStore> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    public async Task EnsureInitialisedAsync(CancellationToken ct = default)
    {
        if (_initialised) return;
        await _writeLock.WaitAsync(ct);
        try
        {
            if (_initialised) return;
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                await pragma.ExecuteNonQueryAsync(ct);
            }
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS clusters (
                    cluster_id              TEXT PRIMARY KEY,
                    cluster_type            INTEGER NOT NULL,
                    member_count            INTEGER NOT NULL,
                    average_bot_probability REAL NOT NULL,
                    average_similarity      REAL NOT NULL,
                    connectedness           REAL NOT NULL,
                    temporal_density        REAL NOT NULL,
                    dominant_country        TEXT,
                    dominant_asn            TEXT,
                    label                   TEXT,
                    description             TEXT,
                    first_seen              TEXT NOT NULL,
                    last_seen               TEXT NOT NULL,
                    dominant_intent         TEXT,
                    average_threat_score    REAL NOT NULL,
                    member_signatures_json  TEXT NOT NULL,
                    updated_at              TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_clusters_last_seen ON clusters(last_seen DESC);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
            _initialised = true;
            _logger.LogInformation("Cluster store initialised at {Path}", _connectionString);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Loads all persisted clusters. Called on <see cref="BotClusterService"/> startup
    ///     to hydrate the in-memory snapshot before the first Leiden cycle runs.
    /// </summary>
    public async Task<IReadOnlyList<BotCluster>> LoadAllAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT cluster_id, cluster_type, member_count, average_bot_probability,
                   average_similarity, connectedness, temporal_density,
                   dominant_country, dominant_asn, label, description,
                   first_seen, last_seen, dominant_intent, average_threat_score,
                   member_signatures_json
              FROM clusters
            """;
        var results = new List<BotCluster>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(ReadCluster(reader));
        }
        return results;
    }

    /// <summary>
    ///     Replaces the entire persisted cluster set atomically (TRUNCATE + bulk INSERT in a
    ///     single transaction). Called by <see cref="BotClusterService"/> after each
    ///     clustering cycle. Snapshot-replace semantics match the in-memory
    ///     <see cref="ClusterSnapshot"/> atomic swap.
    /// </summary>
    public async Task ReplaceAllAsync(IReadOnlyCollection<BotCluster> clusters, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM clusters";
                await del.ExecuteNonQueryAsync(ct);
            }

            if (clusters.Count > 0)
            {
                await using var ins = conn.CreateCommand();
                ins.Transaction = tx;
                ins.CommandText = """
                    INSERT INTO clusters (
                        cluster_id, cluster_type, member_count, average_bot_probability,
                        average_similarity, connectedness, temporal_density,
                        dominant_country, dominant_asn, label, description,
                        first_seen, last_seen, dominant_intent, average_threat_score,
                        member_signatures_json, updated_at)
                    VALUES (
                        @id, @type, @count, @prob, @sim, @conn, @density,
                        @country, @asn, @label, @desc,
                        @first, @last, @intent, @threat,
                        @members, @updated)
                    """;
                var now = DateTime.UtcNow.ToString("O");
                foreach (var c in clusters)
                {
                    ins.Parameters.Clear();
                    ins.Parameters.AddWithValue("@id", c.ClusterId);
                    ins.Parameters.AddWithValue("@type", (int)c.Type);
                    ins.Parameters.AddWithValue("@count", c.MemberCount);
                    ins.Parameters.AddWithValue("@prob", c.AverageBotProbability);
                    ins.Parameters.AddWithValue("@sim", c.AverageSimilarity);
                    ins.Parameters.AddWithValue("@conn", c.Connectedness);
                    ins.Parameters.AddWithValue("@density", c.TemporalDensity);
                    ins.Parameters.AddWithValue("@country", (object?)c.DominantCountry ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@asn", (object?)c.DominantAsn ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@label", (object?)c.Label ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@desc", (object?)c.Description ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@first", c.FirstSeen.ToString("O"));
                    ins.Parameters.AddWithValue("@last", c.LastSeen.ToString("O"));
                    ins.Parameters.AddWithValue("@intent", (object?)c.DominantIntent ?? DBNull.Value);
                    ins.Parameters.AddWithValue("@threat", c.AverageThreatScore);
                    ins.Parameters.AddWithValue("@members",
                        JsonSerializer.Serialize(c.MemberSignatures, Data.BotDetectionJsonSerializerContext.Default.ListString));
                    ins.Parameters.AddWithValue("@updated", now);
                    await ins.ExecuteNonQueryAsync(ct);
                }
            }

            await tx.CommitAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Updates a single cluster's label + description (the LLM-naming write-back path).
    ///     The LLM may return before or after the next snapshot replace; either way the
    ///     persisted name survives because the next ReplaceAllAsync writes the in-memory
    ///     cluster (which has been mutated with the new label by
    ///     <c>BotClusterService.UpdateClusterDescription</c>).
    /// </summary>
    public async Task UpdateLabelAsync(string clusterId, string label, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(clusterId)) return;
        await EnsureInitialisedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE clusters
                   SET label = @label,
                       description = COALESCE(@desc, description),
                       updated_at = @updated
                 WHERE cluster_id = @id
                """;
            cmd.Parameters.AddWithValue("@label", label);
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@updated", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@id", clusterId);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static BotCluster ReadCluster(SqliteDataReader r)
    {
        var membersJson = r.GetString(15);
        var members = string.IsNullOrEmpty(membersJson)
            ? new List<string>()
            : JsonSerializer.Deserialize(membersJson, Data.BotDetectionJsonSerializerContext.Default.ListString) ?? new();
        return new BotCluster
        {
            ClusterId = r.GetString(0),
            Type = (BotClusterType)r.GetInt32(1),
            MemberCount = r.GetInt32(2),
            AverageBotProbability = r.GetDouble(3),
            AverageSimilarity = r.GetDouble(4),
            Connectedness = r.GetDouble(5),
            TemporalDensity = r.GetDouble(6),
            DominantCountry = r.IsDBNull(7) ? null : r.GetString(7),
            DominantAsn = r.IsDBNull(8) ? null : r.GetString(8),
            Label = r.IsDBNull(9) ? null : r.GetString(9),
            Description = r.IsDBNull(10) ? null : r.GetString(10),
            FirstSeen = DateTimeOffset.Parse(r.GetString(11), null, System.Globalization.DateTimeStyles.RoundtripKind),
            LastSeen = DateTimeOffset.Parse(r.GetString(12), null, System.Globalization.DateTimeStyles.RoundtripKind),
            DominantIntent = r.IsDBNull(13) ? null : r.GetString(13),
            AverageThreatScore = r.GetDouble(14),
            MemberSignatures = members
        };
    }
}
