using System.Buffers.Binary;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     SQLite read/write surface for fingerprints, observations, key cache, and corrections.
///     Holds nothing in memory beyond per-call state; durable concurrency is owned by SQLite
///     itself (WAL when enabled). Writes batched at the call site.
/// </summary>
public sealed class SqliteFingerprintStore
{
    private readonly ILogger<SqliteFingerprintStore> _logger;
    private readonly string _connectionString;
    private readonly IdentityVectorLayout _layout;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    public SqliteFingerprintStore(
        ILogger<SqliteFingerprintStore> logger,
        IOptions<BotDetectionOptions> options,
        IdentityVectorLayout layout)
    {
        _logger = logger;
        _layout = layout;
        var dbPath = options.Value.DatabasePath
            ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db");
        var dir = Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(dir);
        var fpDb = Path.Combine(dir, "fingerprints.db");
        // Private cache + WAL gives proper reader/writer concurrency. Shared cache forces
        // serialisation across all connections in-process, which deadlocks when the brute-force
        // index holds a reader on `fingerprints` while the absorption service tries to UPDATE.
        _connectionString = $"Data Source={fpDb}";
    }

    public IdentityVectorLayout Layout => _layout;

    public async Task EnsureInitialisedAsync(CancellationToken ct = default)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
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

            await IdentitySchema.CreateCoreTablesAsync(conn, ct);
            await EnsureLayoutRowAsync(conn, ct);
            _initialised = true;
            _logger.LogInformation(
                "Fingerprint store initialised at {Path}, layout v{Version} dim={Dim}",
                _connectionString, _layout.Version, _layout.Dimension);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task EnsureLayoutRowAsync(SqliteConnection conn, CancellationToken ct)
    {
        await using var read = conn.CreateCommand();
        read.CommandText = "SELECT version, dimension FROM identity_vector_layout WHERE id = 1";
        await using var reader = await read.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
        {
            var ver = reader.GetInt32(0);
            var dim = reader.GetInt32(1);
            if (ver != _layout.Version || dim != _layout.Dimension)
                throw new InvalidOperationException(
                    $"Stored identity_vector_layout (v{ver}, dim={dim}) does not match the running " +
                    $"layout (v{_layout.Version}, dim={_layout.Dimension}). Migrate before starting.");
            return;
        }
        reader.Close();

        await using var insert = conn.CreateCommand();
        insert.CommandText = """
            INSERT INTO identity_vector_layout (id, version, dimension, layout_json, installed_at)
            VALUES (1, @ver, @dim, @json, @ts)
            """;
        insert.Parameters.AddWithValue("@ver", _layout.Version);
        insert.Parameters.AddWithValue("@dim", _layout.Dimension);
        insert.Parameters.AddWithValue("@json",
            System.Text.Json.JsonSerializer.Serialize(_layout.Slots.Select(s => new
            {
                s.Name, s.Offset, s.Width, encoding = s.Encoding.ToString()
            })));
        insert.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>L1 cache lookup by primary signature.</summary>
    public async Task<string?> LookupFingerprintIdAsync(string primarySignature, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fingerprint_id FROM fingerprint_keys WHERE primary_signature = @sig";
        cmd.Parameters.AddWithValue("@sig", primarySignature);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result as string;
    }

    public async Task<Fingerprint?> GetFingerprintAsync(string fingerprintId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, centroid, centroid_maturity, weights, member_count,
                   observation_count, correction_count, first_seen, last_seen, quality,
                   archetype_origin, inferred_client_type, inferred_type_confidence,
                   inferred_type_changed_at, cached_bot_probability, cached_risk_band,
                   cached_score_updated_at
              FROM fingerprints WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return ReadFingerprint(reader);
    }

    /// <summary>Allocate a new fingerprint with the supplied centroid and weights.</summary>
    public async Task InsertFingerprintAsync(Fingerprint fp, string primarySignature, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO fingerprints (
                    fingerprint_id, centroid, centroid_maturity, weights, member_count,
                    observation_count, correction_count, first_seen, last_seen, quality,
                    archetype_origin, inferred_client_type, inferred_type_confidence,
                    inferred_type_changed_at, cached_bot_probability, cached_risk_band,
                    cached_score_updated_at
                ) VALUES (
                    @id, @centroid, @maturity, @weights, @members,
                    @observations, @corrections, @first_seen, @last_seen, @quality,
                    @origin, @inferred_type, @inferred_conf,
                    @inferred_changed, @cached_prob, @cached_band,
                    @cached_updated
                )
                """;
            cmd.Parameters.AddWithValue("@id", fp.FingerprintId);
            cmd.Parameters.AddWithValue("@centroid", FloatsToBlob(fp.Centroid));
            cmd.Parameters.AddWithValue("@maturity", fp.CentroidMaturity);
            cmd.Parameters.AddWithValue("@weights", FloatsToBlob(fp.Weights));
            cmd.Parameters.AddWithValue("@members", fp.MemberCount);
            cmd.Parameters.AddWithValue("@observations", fp.ObservationCount);
            cmd.Parameters.AddWithValue("@corrections", fp.CorrectionCount);
            cmd.Parameters.AddWithValue("@first_seen", fp.FirstSeen.ToString("O"));
            cmd.Parameters.AddWithValue("@last_seen", fp.LastSeen.ToString("O"));
            cmd.Parameters.AddWithValue("@quality", fp.Quality);
            cmd.Parameters.AddWithValue("@origin", (object?)fp.ArchetypeOrigin ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@inferred_type", fp.InferredClientType);
            cmd.Parameters.AddWithValue("@inferred_conf", fp.InferredTypeConfidence);
            cmd.Parameters.AddWithValue("@inferred_changed", fp.InferredTypeChangedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@cached_prob", fp.CachedBotProbability);
            cmd.Parameters.AddWithValue("@cached_band", (object?)fp.CachedRiskBand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cached_updated",
                (object?)fp.CachedScoreUpdatedAt?.ToString("O") ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await UpsertKeyAsync(conn, tx, primarySignature, fp.FingerprintId, ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>Insert or update fingerprint_keys binding a primary_signature to a fingerprint_id.</summary>
    public async Task UpsertKeyAsync(string primarySignature, string fingerprintId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await UpsertKeyAsync(conn, null, primarySignature, fingerprintId, ct);
    }

    private static async Task UpsertKeyAsync(
        SqliteConnection conn, SqliteTransaction? tx,
        string primarySignature, string fingerprintId, CancellationToken ct)
    {
        var now = DateTime.UtcNow.ToString("O");
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            INSERT INTO fingerprint_keys (primary_signature, fingerprint_id, first_seen, last_seen, hit_count)
                VALUES (@sig, @id, @now, @now, 1)
                ON CONFLICT(primary_signature) DO UPDATE SET
                    fingerprint_id = excluded.fingerprint_id,
                    last_seen      = excluded.last_seen,
                    hit_count      = hit_count + 1
            """;
        cmd.Parameters.AddWithValue("@sig", primarySignature);
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        cmd.Parameters.AddWithValue("@now", now);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Append an unabsorbed observation row.</summary>
    public async Task RecordObservationAsync(string fingerprintId, float[] vector, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_observations (fingerprint_id, vector, observed_at, absorbed_at)
            VALUES (@id, @vec, @ts, NULL)
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        cmd.Parameters.AddWithValue("@vec", FloatsToBlob(vector));
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);

        await using var bump = conn.CreateCommand();
        bump.CommandText = """
            UPDATE fingerprints
               SET observation_count = observation_count + 1,
                   last_seen = @ts
             WHERE fingerprint_id = @id
            """;
        bump.Parameters.AddWithValue("@id", fingerprintId);
        bump.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        await bump.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Record a Pass-2-corrects-Pass-1 disagreement and persist Pass 2's updated weights.</summary>
    public async Task RecordCorrectionAsync(
        string requestId,
        string primarySignature,
        string? pass1FingerprintId,
        string pass2FingerprintId,
        float[] differentiator,
        float[] updatedPass2Weights,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO fingerprint_corrections
                    (request_id, primary_signature, pass1_fingerprint, pass2_fingerprint,
                     differentiator, observed_at)
                VALUES (@req, @sig, @p1, @p2, @diff, @ts)
                """;
            cmd.Parameters.AddWithValue("@req", requestId);
            cmd.Parameters.AddWithValue("@sig", primarySignature);
            cmd.Parameters.AddWithValue("@p1", (object?)pass1FingerprintId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@p2", pass2FingerprintId);
            cmd.Parameters.AddWithValue("@diff", FloatsToBlob(differentiator));
            cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await using (var bump = conn.CreateCommand())
        {
            bump.Transaction = tx;
            bump.CommandText = """
                UPDATE fingerprints
                   SET weights = @weights,
                       correction_count = correction_count + 1
                 WHERE fingerprint_id = @id
                """;
            bump.Parameters.AddWithValue("@weights", FloatsToBlob(updatedPass2Weights));
            bump.Parameters.AddWithValue("@id", pass2FingerprintId);
            await bump.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    ///     Absorption transaction: fold the supplied observation into the fingerprint's centroid
    ///     using maturity-weighted mean, mark the obs row absorbed, persist the updated weights.
    ///     If <paramref name="newInferredClientType"/> is non-null and differs from the current
    ///     row, also updates inferred_client_type / inferred_type_confidence /
    ///     inferred_type_changed_at in the same transaction.
    /// </summary>
    public async Task AbsorbObservationAsync(
        long observationId,
        string fingerprintId,
        float[] newCentroid,
        int newMaturity,
        float[] newWeights,
        string? newInferredClientType = null,
        double newInferredTypeConfidence = 0,
        bool inferredTypeChanged = false,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var fp = conn.CreateCommand())
        {
            fp.Transaction = tx;
            if (newInferredClientType is not null)
            {
                fp.CommandText = """
                    UPDATE fingerprints
                       SET centroid                 = @centroid,
                           centroid_maturity        = @maturity,
                           weights                  = @weights,
                           inferred_client_type     = @itype,
                           inferred_type_confidence = @iconf,
                           inferred_type_changed_at = CASE WHEN @ichanged THEN @now
                                                            ELSE inferred_type_changed_at END
                     WHERE fingerprint_id = @id
                    """;
                fp.Parameters.AddWithValue("@itype", newInferredClientType);
                fp.Parameters.AddWithValue("@iconf", newInferredTypeConfidence);
                fp.Parameters.AddWithValue("@ichanged", inferredTypeChanged);
                fp.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            }
            else
            {
                fp.CommandText = """
                    UPDATE fingerprints
                       SET centroid          = @centroid,
                           centroid_maturity = @maturity,
                           weights           = @weights
                     WHERE fingerprint_id = @id
                    """;
            }
            fp.Parameters.AddWithValue("@centroid", FloatsToBlob(newCentroid));
            fp.Parameters.AddWithValue("@maturity", newMaturity);
            fp.Parameters.AddWithValue("@weights", FloatsToBlob(newWeights));
            fp.Parameters.AddWithValue("@id", fingerprintId);
            await fp.ExecuteNonQueryAsync(ct);
        }

        await using (var obs = conn.CreateCommand())
        {
            obs.Transaction = tx;
            obs.CommandText = "UPDATE fingerprint_observations SET absorbed_at = @ts WHERE id = @id";
            obs.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
            obs.Parameters.AddWithValue("@id", observationId);
            await obs.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <summary>
    ///     Returns observations that meet the maturity threshold (the fingerprint has had
    ///     <paramref name="maturityThreshold"/> additional observations since this one was
    ///     recorded) OR are older than <paramref name="ageDays"/> on an active fingerprint.
    ///     Active = the fingerprint has been observed within <paramref name="activeWindowDays"/>.
    ///
    ///     Materialised before return so the reader closes before any caller starts writing.
    /// </summary>
    public async Task<IReadOnlyList<AbsorbableObservation>> ListAbsorbableObservationsAsync(
        int maturityThreshold,
        int ageDays,
        int activeWindowDays,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var results = new List<AbsorbableObservation>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT o.id, o.fingerprint_id, o.vector, o.observed_at,
                   f.centroid, f.centroid_maturity, f.weights, f.observation_count, f.last_seen,
                   f.inferred_client_type
              FROM fingerprint_observations o
              JOIN fingerprints f ON f.fingerprint_id = o.fingerprint_id
             WHERE o.absorbed_at IS NULL
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var ageCutoff = DateTime.UtcNow.AddDays(-ageDays);
        var activeCutoff = DateTime.UtcNow.AddDays(-activeWindowDays);

        while (await reader.ReadAsync(ct))
        {
            var observedAt = DateTime.Parse(reader.GetString(3), null,
                System.Globalization.DateTimeStyles.RoundtripKind);
            var lastSeen = DateTime.Parse(reader.GetString(8), null,
                System.Globalization.DateTimeStyles.RoundtripKind);
            var observationCount = reader.GetInt32(7);

            // No per-row "observations since this one" cheaply; approximate by fingerprint's
            // lifetime observation_count. Anything past the maturity threshold is eligible; we
            // also accept old rows on active fingerprints. The brute-force scan picks them up
            // each tick.
            var maturityFired = observationCount >= maturityThreshold;
            var ageFired = observedAt <= ageCutoff && lastSeen >= activeCutoff;
            if (!maturityFired && !ageFired) continue;

            results.Add(new AbsorbableObservation
            {
                ObservationId = reader.GetInt64(0),
                FingerprintId = reader.GetString(1),
                Vector = BlobToFloats((byte[])reader.GetValue(2)),
                Centroid = BlobToFloats((byte[])reader.GetValue(4)),
                CentroidMaturity = reader.GetInt32(5),
                Weights = BlobToFloats((byte[])reader.GetValue(6)),
                InferredClientType = reader.GetString(9)
            });
        }
        return results;
    }

    /// <summary>List all fingerprints. Materialised; reader closes before return.</summary>
    public async Task<IReadOnlyList<Fingerprint>> ListFingerprintsAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var results = new List<Fingerprint>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, centroid, centroid_maturity, weights, member_count,
                   observation_count, correction_count, first_seen, last_seen, quality,
                   archetype_origin, inferred_client_type, inferred_type_confidence,
                   inferred_type_changed_at, cached_bot_probability, cached_risk_band,
                   cached_score_updated_at
              FROM fingerprints
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadFingerprint(reader));
        return results;
    }

    /// <summary>List unabsorbed observation vectors. Materialised; reader closes before return.</summary>
    public async Task<IReadOnlyList<(string FingerprintId, float[] Vector)>> ListActiveObservationsAsync(
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var results = new List<(string, float[])>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, vector
              FROM fingerprint_observations
             WHERE absorbed_at IS NULL
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var blob = (byte[])reader.GetValue(1);
            results.Add((id, BlobToFloats(blob)));
        }
        return results;
    }

    /// <summary>
    ///     Lists fingerprints whose cached_score_updated_at is null or older than
    ///     <paramref name="ttlSeconds"/>, capped at <paramref name="batchSize"/>. Returned in
    ///     oldest-checked-first order so the longest-stale fingerprints are re-verified first.
    ///     Skips fingerprints with no observation rows (nothing for the drift service to compare).
    ///     Materialised; reader closes before return.
    /// </summary>
    public async Task<IReadOnlyList<Fingerprint>> ListStaleScoreFingerprintsAsync(
        int ttlSeconds, int batchSize, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var cutoff = DateTime.UtcNow.AddSeconds(-Math.Max(1, ttlSeconds)).ToString("O");
        var results = new List<Fingerprint>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, centroid, centroid_maturity, weights, member_count,
                   observation_count, correction_count, first_seen, last_seen, quality,
                   archetype_origin, inferred_client_type, inferred_type_confidence,
                   inferred_type_changed_at, cached_bot_probability, cached_risk_band,
                   cached_score_updated_at
              FROM fingerprints
             WHERE observation_count > 0
               AND (cached_score_updated_at IS NULL OR cached_score_updated_at < @cutoff)
             ORDER BY COALESCE(cached_score_updated_at, '0001-01-01T00:00:00Z') ASC
             LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, batchSize));
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadFingerprint(reader));
        return results;
    }

    /// <summary>
    ///     Returns the most recent observation vector for the fingerprint regardless of absorption
    ///     state, or null if it has no observations. Used by the drift service to re-verify the
    ///     fingerprint's most recent behaviour against its centroid + weights.
    /// </summary>
    public async Task<float[]?> GetLatestObservationVectorAsync(
        string fingerprintId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT vector FROM fingerprint_observations
             WHERE fingerprint_id = @id
             ORDER BY id DESC
             LIMIT 1
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        var blob = await cmd.ExecuteScalarAsync(ct);
        return blob is byte[] bytes ? BlobToFloats(bytes) : null;
    }

    /// <summary>
    ///     Marks the fingerprint as re-verified. The drift service calls this after every check
    ///     regardless of outcome, so a noisy-but-stable fingerprint doesn't get re-checked every
    ///     tick. Drift-detected fingerprints will be picked up again on the next TTL expiry.
    /// </summary>
    public async Task BumpCachedScoreCheckedAtAsync(string fingerprintId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprints SET cached_score_updated_at = @ts
             WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private Fingerprint ReadFingerprint(SqliteDataReader reader) => new()
    {
        FingerprintId = reader.GetString(0),
        Centroid = BlobToFloats((byte[])reader.GetValue(1)),
        CentroidMaturity = reader.GetInt32(2),
        Weights = BlobToFloats((byte[])reader.GetValue(3)),
        MemberCount = reader.GetInt32(4),
        ObservationCount = reader.GetInt32(5),
        CorrectionCount = reader.GetInt32(6),
        FirstSeen = DateTime.Parse(reader.GetString(7), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LastSeen = DateTime.Parse(reader.GetString(8), null, System.Globalization.DateTimeStyles.RoundtripKind),
        Quality = reader.GetDouble(9),
        ArchetypeOrigin = reader.IsDBNull(10) ? null : reader.GetString(10),
        InferredClientType = reader.GetString(11),
        InferredTypeConfidence = reader.GetDouble(12),
        InferredTypeChangedAt = DateTime.Parse(reader.GetString(13), null, System.Globalization.DateTimeStyles.RoundtripKind),
        CachedBotProbability = reader.GetDouble(14),
        CachedRiskBand = reader.IsDBNull(15) ? null : reader.GetString(15),
        CachedScoreUpdatedAt = reader.IsDBNull(16)
            ? null
            : DateTime.Parse(reader.GetString(16), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };

    internal static byte[] FloatsToBlob(float[] values)
    {
        var bytes = new byte[values.Length * sizeof(float)];
        for (var i = 0; i < values.Length; i++)
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), values[i]);
        return bytes;
    }

    internal static float[] BlobToFloats(byte[] blob)
    {
        var values = new float[blob.Length / sizeof(float)];
        for (var i = 0; i < values.Length; i++)
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(blob.AsSpan(i * sizeof(float)));
        return values;
    }
}
