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
// Not sealed: remote-mode dashboards register a HTTP-backed IFingerprintReader instead
// of this concrete type. Base class continues to own the write path (centroid updates,
// observation absorption, score caching) which remote viewers never call.
public class SqliteFingerprintStore : IFingerprintReader
{
    private readonly ILogger<SqliteFingerprintStore> _logger;
    private readonly string _connectionString;
    private readonly IdentityVectorLayout _layout;
    private readonly IdentityEngineOptions _engineOptions;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;
    private bool _vecAvailable;

    public SqliteFingerprintStore(
        ILogger<SqliteFingerprintStore> logger,
        IOptions<BotDetectionOptions> options,
        IdentityVectorLayout layout)
    {
        _logger = logger;
        _layout = layout;
        _engineOptions = options.Value.Identity.Engine;
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

    /// <summary>
    ///     True when the sqlite-vec extension loaded successfully on init and the vec0
    ///     virtual tables were created. Read by <c>SqliteVecIdentityAnchorIndex</c> to
    ///     decide whether to dispatch to vec0 KNN or fall through to brute force.
    /// </summary>
    public bool IsVecAvailable => _vecAvailable;

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
            await IdentitySchema.MigrateExistingTablesAsync(conn, ct);
            await EnsureLayoutRowAsync(conn, ct);

            // Best-effort sqlite-vec load. The brute-force index is the FOSS default;
            // operators install asg017/sqlite-vec themselves to opt into the perf path.
            // Failure to load is informational, never fatal.
            if (_engineOptions.PreferSqliteVec)
                _vecAvailable = await TryLoadVecExtensionAsync(conn, ct);

            _initialised = true;
            _logger.LogInformation(
                "Fingerprint store initialised at {Path}, layout v{Version} dim={Dim}, sqlite-vec={Vec}",
                _connectionString, _layout.Version, _layout.Dimension,
                _vecAvailable ? "enabled" : "unavailable (brute force)");
        }
        finally
        {
            _initLock.Release();
        }
    }

    /// <summary>
    ///     Attempts to load the sqlite-vec extension on the supplied connection and create
    ///     the vec0 virtual indexes. Returns true on success. Failure is silent at WARN
    ///     level — the brute-force index will pick up where vec0 didn't.
    /// </summary>
    private async Task<bool> TryLoadVecExtensionAsync(SqliteConnection conn, CancellationToken ct)
    {
        try
        {
            conn.EnableExtensions(true);
            // Either operator-supplied path or the OS library search path.
            var extName = _engineOptions.SqliteVecExtensionPath ?? "vec0";
            conn.LoadExtension(extName);
            await IdentitySchema.CreateVecIndexesAsync(conn, _layout.Dimension, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogInformation(
                "sqlite-vec extension not available ({Reason}); using brute-force anchor index. " +
                "Install from https://github.com/asg017/sqlite-vec/releases to opt into the vec0 perf path.",
                ex.GetType().Name);
            return false;
        }
    }

    /// <summary>
    ///     Opens a connection and, when sqlite-vec was successfully loaded at init time,
    ///     re-loads the extension on this connection so vec0 queries work. Each
    ///     <see cref="SqliteConnection"/> needs the extension loaded independently —
    ///     loading on one connection doesn't propagate.
    /// </summary>
    private async Task<SqliteConnection> OpenConnectionWithVecAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        if (!_vecAvailable) return conn;
        try
        {
            conn.EnableExtensions(true);
            conn.LoadExtension(_engineOptions.SqliteVecExtensionPath ?? "vec0");
        }
        catch (Exception ex)
        {
            // Per-connection load failure shouldn't crash the request — fall back silently.
            _logger.LogWarning(ex, "Per-connection sqlite-vec load failed; vec queries on this connection will fail");
        }
        return conn;
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
        insert.Parameters.AddWithValue("@json", BuildLayoutJson(_layout.Slots));
        insert.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        await insert.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Single-roundtrip cached-verdict lookup by primary signature. Joins
    ///     <c>fingerprint_keys</c> to <c>fingerprints</c> and projects only the columns
    ///     the verdict gate consumes (no <c>centroid</c> blob). Returns null when no
    ///     fingerprint is bound to this signature, or when it has never had its cached
    ///     score written.
    /// </summary>
    public async Task<IdentityCachedVerdict?> GetCachedVerdictForSignatureAsync(
        string primarySignature, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(primarySignature)) return null;
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT f.fingerprint_id, f.cached_bot_probability, f.cached_risk_band,
                   f.cached_score_updated_at, f.observation_count, f.inferred_client_type
              FROM fingerprint_keys k
              JOIN fingerprints f ON f.fingerprint_id = k.fingerprint_id
             WHERE k.primary_signature = @sig
               AND f.cached_score_updated_at IS NOT NULL
            """;
        cmd.Parameters.AddWithValue("@sig", primarySignature);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        return new IdentityCachedVerdict(
            FingerprintId: reader.GetString(0),
            BotProbability: reader.GetDouble(1),
            RiskBand: reader.IsDBNull(2) ? null : reader.GetString(2),
            UpdatedAtUtc: DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
            ObservationCount: reader.GetInt32(4),
            InferredClientType: reader.GetString(5));
    }

    /// <summary>Count of unabsorbed observation rows for a single fingerprint.</summary>
    public async Task<int> GetUnabsorbedObservationCountAsync(string fingerprintId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*) FROM fingerprint_observations
             WHERE fingerprint_id = @id AND absorbed_at IS NULL
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return Convert.ToInt32(result ?? 0);
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
                   cached_score_updated_at, ambiguity_persistence,
                   display_name, display_name_updated_at
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
        await using var conn = await OpenConnectionWithVecAsync(ct);
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
                    cached_score_updated_at, ambiguity_persistence,
                    display_name, display_name_updated_at
                ) VALUES (
                    @id, @centroid, @maturity, @weights, @members,
                    @observations, @corrections, @first_seen, @last_seen, @quality,
                    @origin, @inferred_type, @inferred_conf,
                    @inferred_changed, @cached_prob, @cached_band,
                    @cached_updated, @ambiguity,
                    @display_name, @display_name_updated
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
            cmd.Parameters.AddWithValue("@ambiguity", fp.AmbiguityPersistence);
            cmd.Parameters.AddWithValue("@display_name", fp.DisplayName ?? "");
            cmd.Parameters.AddWithValue("@display_name_updated",
                fp.DisplayNameUpdatedAt == default ? "" : fp.DisplayNameUpdatedAt.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await UpsertKeyAsync(conn, tx, primarySignature, fp.FingerprintId, ct);

        if (_vecAvailable)
        {
            await using var vec = conn.CreateCommand();
            vec.Transaction = tx;
            vec.CommandText = "INSERT INTO fingerprints_vec(fingerprint_id, centroid) VALUES (@id, @vec)";
            vec.Parameters.AddWithValue("@id", fp.FingerprintId);
            vec.Parameters.AddWithValue("@vec", FloatsToBlob(fp.Centroid));
            await vec.ExecuteNonQueryAsync(ct);
        }

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

    /// <summary>
    ///     Updates a fingerprint's display name and timestamp. Called from
    ///     <c>FingerprintMatchContributor</c> on two paths: (1) lazy backfill when a row
    ///     migrated from before the column existed is matched and its <c>DisplayName</c>
    ///     is empty; (2) significant-drift recompute (drift score above
    ///     <c>Match.SignificantDriftEpsilon</c>). Idempotent; no-op when the row doesn't
    ///     exist.
    /// </summary>
    public async Task UpdateDisplayNameAsync(
        string fingerprintId, string displayName, DateTime updatedAt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprints
               SET display_name = @name,
                   display_name_updated_at = @ts
             WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@name", displayName ?? "");
        cmd.Parameters.AddWithValue("@ts", updatedAt.ToString("O"));
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Update the display name on whichever fingerprint <paramref name="primarySignature"/>
    ///     currently maps to. One-shot helper for downstream consumers (the LLM-result callback,
    ///     dashboard "rename" controls) that have a signature in hand but not a fingerprint id.
    ///     Idempotent; no-op when the signature isn't bound to any fingerprint.
    /// </summary>
    public async Task UpdateDisplayNameForSignatureAsync(
        string primarySignature, string displayName, DateTime updatedAt, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(primarySignature)) return;
        var fingerprintId = await LookupFingerprintIdAsync(primarySignature, ct);
        if (fingerprintId is null) return;
        await UpdateDisplayNameAsync(fingerprintId, displayName, updatedAt, ct);
    }

    /// <summary>Append an unabsorbed observation row.</summary>
    public async Task RecordObservationAsync(string fingerprintId, float[] vector, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_observations (fingerprint_id, vector, observed_at, absorbed_at)
            VALUES (@id, @vec, @ts, NULL);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        cmd.Parameters.AddWithValue("@vec", FloatsToBlob(vector));
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        var observationId = (long)(await cmd.ExecuteScalarAsync(ct))!;

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

        if (_vecAvailable)
        {
            await using var vec = conn.CreateCommand();
            vec.CommandText = "INSERT INTO observations_vec(observation_id, fingerprint_id, vector) VALUES (@oid, @fid, @v)";
            vec.Parameters.AddWithValue("@oid", observationId);
            vec.Parameters.AddWithValue("@fid", fingerprintId);
            vec.Parameters.AddWithValue("@v", FloatsToBlob(vector));
            await vec.ExecuteNonQueryAsync(ct);
        }
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
        await using var conn = await OpenConnectionWithVecAsync(ct);
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

        if (_vecAvailable)
        {
            // Update the centroid in vec0 — UPSERT shape: delete-then-insert is the
            // simplest reliable way to push a new centroid into vec0, since vec0's UPDATE
            // syntax has version-dependent behaviour for the vector column.
            await using (var del = conn.CreateCommand())
            {
                del.Transaction = tx;
                del.CommandText = "DELETE FROM fingerprints_vec WHERE fingerprint_id = @id";
                del.Parameters.AddWithValue("@id", fingerprintId);
                await del.ExecuteNonQueryAsync(ct);
            }
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO fingerprints_vec(fingerprint_id, centroid) VALUES (@id, @vec)";
                ins.Parameters.AddWithValue("@id", fingerprintId);
                ins.Parameters.AddWithValue("@vec", FloatsToBlob(newCentroid));
                await ins.ExecuteNonQueryAsync(ct);
            }
            // Drop the absorbed observation from the active vec0 index — it's been
            // folded into the centroid; keeping it would double-count in KNN searches.
            await using (var obsDel = conn.CreateCommand())
            {
                obsDel.Transaction = tx;
                obsDel.CommandText = "DELETE FROM observations_vec WHERE observation_id = @id";
                obsDel.Parameters.AddWithValue("@id", observationId);
                await obsDel.ExecuteNonQueryAsync(ct);
            }
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
                   cached_score_updated_at, ambiguity_persistence,
                   display_name, display_name_updated_at
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
                   cached_score_updated_at, ambiguity_persistence,
                   display_name, display_name_updated_at
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
    ///     Writes a new cached verdict to the fingerprint row. Used by the manual AI opinion
    ///     path so an operator-triggered classifier verdict updates the row live without
    ///     waiting for the next drift tick. Touches <c>cached_bot_probability</c>,
    ///     <c>cached_risk_band</c>, and <c>cached_score_updated_at</c> in one transaction.
    /// </summary>
    public async Task UpdateCachedVerdictAsync(
        string fingerprintId, double botProbability, string? riskBand, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprints
               SET cached_bot_probability  = @prob,
                   cached_risk_band        = @band,
                   cached_score_updated_at = @ts
             WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@prob", botProbability);
        cmd.Parameters.AddWithValue("@band", (object?)riskBand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Test-only utility: truncates every identity table so a BDF rig can replay
    ///     scenarios against a deterministic clean state. Returns per-table row counts
    ///     deleted. Vec0 mirror tables are also truncated when the extension loaded.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> TruncateAllAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var tables = new[]
        {
            "fingerprint_corrections",
            "fingerprint_observations",
            "fingerprint_keys",
            "fingerprints",
            "identity_dimension_weights",
            "identity_archetypes"
        };
        foreach (var table in tables)
        {
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"DELETE FROM {table}";
            counts[table] = await cmd.ExecuteNonQueryAsync(ct);
        }

        if (_vecAvailable)
        {
            foreach (var vecTable in new[] { "observations_vec", "fingerprints_vec" })
            {
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.Transaction = tx;
                    cmd.CommandText = $"DELETE FROM {vecTable}";
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (SqliteException) { /* table may not exist if vec0 schema not created */ }
            }
        }

        await tx.CommitAsync(ct);
        return counts;
    }

    /// <summary>
    ///     Atomically EWMA-updates the per-fingerprint ambiguity-persistence value and
    ///     returns the post-update value. <paramref name="isAmbiguityEvent"/> = true pushes
    ///     toward 1 (Pass 2 correction, rotation candidate, L1 confirm fail, allocation),
    ///     false pushes toward 0 (clean L1 confirm success). EWMA is computed in SQL so
    ///     concurrent writers can't lose updates — SQLite serialises UPDATEs to the same
    ///     row. Uses RETURNING for the atomic post-write read.
    /// </summary>
    public async Task<double> BumpAmbiguityPersistenceAsync(
        string fingerprintId, bool isAmbiguityEvent, double alpha, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprints
               SET ambiguity_persistence = ((1 - @alpha) * ambiguity_persistence) + (@alpha * @ev)
             WHERE fingerprint_id = @id
            RETURNING ambiguity_persistence
            """;
        cmd.Parameters.AddWithValue("@alpha", alpha);
        cmd.Parameters.AddWithValue("@ev", isAmbiguityEvent ? 1.0 : 0.0);
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null or DBNull ? 0.0 : Convert.ToDouble(result);
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

    /// <summary>
    ///     Batch lookup: resolves each primary signature to its fingerprint's centroid in
    ///     a single round-trip. Used by <c>BotClusterService</c> as the behavioural-vector
    ///     axis for similarity scoring — the metastable centroid is the actual learned
    ///     shape, replacing the prior text-embedding hack. Signatures with no fingerprint
    ///     binding are absent from the result; callers fall back to heuristic-only
    ///     similarity for them.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, float[]>> GetCentroidsBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures, CancellationToken ct = default)
    {
        if (primarySignatures.Count == 0)
            return new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);

        await EnsureInitialisedAsync(ct);
        var result = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Inline IN-clause via parameterised placeholders. SQLite tolerates large IN lists
        // (limit defaults to 250k); cluster batches are O(hundreds), well under.
        var sb = new System.Text.StringBuilder();
        sb.Append("""
            SELECT k.primary_signature, f.centroid
              FROM fingerprint_keys k
              JOIN fingerprints f ON f.fingerprint_id = k.fingerprint_id
             WHERE k.primary_signature IN (
            """);
        var i = 0;
        foreach (var _ in primarySignatures)
        {
            if (i > 0) sb.Append(',');
            sb.Append('@').Append('p').Append(i);
            i++;
        }
        sb.Append(')');

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        i = 0;
        foreach (var sig in primarySignatures)
        {
            cmd.Parameters.AddWithValue($"@p{i}", sig);
            i++;
        }

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = BlobToFloats((byte[])reader.GetValue(1));
        return result;
    }

    /// <summary>
    ///     Counts of unabsorbed observation rows grouped by fingerprint id. Returned as a
    ///     dictionary keyed by fingerprint id so a dashboard listing can join in C# without
    ///     N+1 queries. Materialised; reader closes before return.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, int>> GetUnabsorbedObservationCountsAsync(
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, COUNT(*) AS unabsorbed
              FROM fingerprint_observations
             WHERE absorbed_at IS NULL
             GROUP BY fingerprint_id
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            result[reader.GetString(0)] = reader.GetInt32(1);
        return result;
    }

    /// <summary>
    ///     Persist the calibrated global per-dim weight vector. Single-row table; replaces any
    ///     existing weights atomically. Read by the matcher via
    ///     <see cref="GetGlobalWeightsAsync"/> on its refresh cadence.
    /// </summary>
    public async Task UpsertGlobalWeightsAsync(
        float[] weights, int samplesUsed, int clustersUsed, int archetypesUsed,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity_dimension_weights
                (id, weights, samples_used, clusters_used, archetypes_used, last_computed_at)
                VALUES (1, @w, @samples, @clusters, @archetypes, @ts)
                ON CONFLICT(id) DO UPDATE SET
                    weights          = excluded.weights,
                    samples_used     = excluded.samples_used,
                    clusters_used    = excluded.clusters_used,
                    archetypes_used  = excluded.archetypes_used,
                    last_computed_at = excluded.last_computed_at
            """;
        cmd.Parameters.AddWithValue("@w", FloatsToBlob(weights));
        cmd.Parameters.AddWithValue("@samples", samplesUsed);
        cmd.Parameters.AddWithValue("@clusters", clustersUsed);
        cmd.Parameters.AddWithValue("@archetypes", archetypesUsed);
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Read the calibrated global per-dim weight vector. Returns null when calibration has
    ///     never run; the matcher should fall back to all-1.0 in that case.
    /// </summary>
    public async Task<(float[] Weights, DateTime LastComputedAt)?> GetGlobalWeightsAsync(
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT weights, last_computed_at FROM identity_dimension_weights WHERE id = 1";
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var blob = (byte[])reader.GetValue(0);
        var ts = DateTime.Parse(reader.GetString(1), null, System.Globalization.DateTimeStyles.RoundtripKind);
        return (BlobToFloats(blob), ts);
    }

    /// <summary>
    ///     Persist a refined archetype centroid + descendant count + last_refined_at. The mask
    ///     is left as-is — only the YAML loader sets it (the dims an archetype asserts don't
    ///     change with refinement).
    /// </summary>
    public async Task UpsertArchetypeAsync(IdentityArchetype archetype, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity_archetypes
                (archetype_id, name, description, centroid, dimension_mask, archetype_kind,
                 descendant_count, last_refined_at)
                VALUES (@id, @name, @desc, @centroid, @mask, @kind, @count, @ts)
                ON CONFLICT(archetype_id) DO UPDATE SET
                    centroid         = excluded.centroid,
                    descendant_count = excluded.descendant_count,
                    last_refined_at  = excluded.last_refined_at
            """;
        cmd.Parameters.AddWithValue("@id", archetype.ArchetypeId);
        cmd.Parameters.AddWithValue("@name", archetype.Name);
        cmd.Parameters.AddWithValue("@desc", (object?)archetype.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@centroid", FloatsToBlob(archetype.Centroid));
        cmd.Parameters.AddWithValue("@mask", FloatsToBlob(archetype.DimensionMask));
        cmd.Parameters.AddWithValue("@kind", archetype.ArchetypeKind);
        cmd.Parameters.AddWithValue("@count", archetype.DescendantCount);
        cmd.Parameters.AddWithValue("@ts", archetype.LastRefinedAt.ToString("O"));
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
            : DateTime.Parse(reader.GetString(16), null, System.Globalization.DateTimeStyles.RoundtripKind),
        AmbiguityPersistence = reader.GetDouble(17),
        DisplayName = reader.GetString(18),
        DisplayNameUpdatedAt = string.IsNullOrEmpty(reader.GetString(19))
            ? default
            : DateTime.Parse(reader.GetString(19), null, System.Globalization.DateTimeStyles.RoundtripKind)
    };

    /// <summary>
    ///     Layout JSON is diagnostic — written once at first init for forensics, never
    ///     re-read by the code. Hand-written via <see cref="System.Text.Json.Utf8JsonWriter"/>
    ///     to stay AOT-clean (no anonymous-type reflection).
    /// </summary>
    private static string BuildLayoutJson(IReadOnlyList<IdentityVectorSlot> slots)
    {
        using var ms = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(ms))
        {
            w.WriteStartArray();
            foreach (var s in slots)
            {
                w.WriteStartObject();
                w.WriteString("Name", s.Name);
                w.WriteNumber("Offset", s.Offset);
                w.WriteNumber("Width", s.Width);
                w.WriteString("encoding", s.Encoding.ToString());
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(ms.ToArray());
    }

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

    /// <summary>
    ///     vec0 KNN over the centroid index. Returns (fingerprint_id, l2_distance) pairs
    ///     ordered ascending by distance, capped at <paramref name="k"/>. Caller translates
    ///     distance to cosine. Throws if <see cref="IsVecAvailable"/> is false.
    /// </summary>
    public async Task<IReadOnlyList<(string FingerprintId, double Distance)>> SearchVecCentroidsAsync(
        float[] vector, int k, CancellationToken ct = default)
    {
        var results = new List<(string, double)>(k);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, distance FROM fingerprints_vec
             WHERE centroid MATCH @vec AND k = @k
            """;
        cmd.Parameters.AddWithValue("@vec", FloatsToBlob(vector));
        cmd.Parameters.AddWithValue("@k", k);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetDouble(1)));
        return results;
    }

    /// <summary>vec0 KNN over the unabsorbed observation index. Same shape as the centroid variant.</summary>
    public async Task<IReadOnlyList<(string FingerprintId, double Distance)>> SearchVecObservationsAsync(
        float[] vector, int k, CancellationToken ct = default)
    {
        var results = new List<(string, double)>(k);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT fingerprint_id, distance FROM observations_vec
             WHERE vector MATCH @vec AND k = @k
            """;
        cmd.Parameters.AddWithValue("@vec", FloatsToBlob(vector));
        cmd.Parameters.AddWithValue("@k", k);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetDouble(1)));
        return results;
    }
}

