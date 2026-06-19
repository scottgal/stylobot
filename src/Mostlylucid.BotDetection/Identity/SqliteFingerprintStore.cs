using System.Buffers.Binary;
using System.Collections.Concurrent;
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
public class SqliteFingerprintStore : IFingerprintStore
{
    private readonly ILogger<SqliteFingerprintStore> _logger;
    private readonly string _connectionString;
    private readonly IdentityVectorLayout _layout;
    private readonly IdentityEngineOptions _engineOptions;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;
    private bool _vecAvailable;

    private readonly string _dataDir;

    // ----------------------------------------------------------------------
    // LFU façade caches. Pattern matches SqlitePathLifecycleStore: dict is
    // truth on the hot read path, SQLite is durability. Closes the parallel-
    // burst race (Bug O / P / Q) where N HTTP/2 asset fetches for the same
    // primarySig each opened a fresh SqliteConnection and re-queried the same
    // row, with some reads landing while a write was in flight and returning
    // null. The caches are bounded and entries are invalidated by the write
    // paths so a row update is visible to the next read.
    // ----------------------------------------------------------------------
    private const int FingerprintIdCacheMaxEntries = 50_000;
    private const int FingerprintCacheMaxEntries = 10_000;

    // Raised after RecordObservationAsync commits; Task 4 subscriber wakes on this.
    public event Action<string>? ObservationAppended;

    private readonly ConcurrentDictionary<string, string> _fingerprintIdByPrimarySig
        = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Fingerprint> _fingerprintById
        = new(StringComparer.Ordinal);

    // Per-cache invalidation epoch. Readers snapshot the epoch BEFORE the DB
    // query and recheck after; if a writer invalidated the cache during the
    // read, the populate is skipped so a stale value can never overwrite a
    // freshly-invalidated slot. Pattern closes the classic
    // read-populate-vs-write-invalidate race that plain TryAdd would leave open.
    private long _fingerprintIdEpoch;
    private long _fingerprintEpoch;

    private void InvalidateFingerprintCache(string fingerprintId)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;
        _fingerprintById.TryRemove(fingerprintId, out _);
        System.Threading.Interlocked.Increment(ref _fingerprintEpoch);
    }

    private static void EvictOldest<TKey, TValue>(
        ConcurrentDictionary<TKey, TValue> dict,
        int targetSize) where TKey : notnull
    {
        // Cheap eviction: drop a slice from whatever the dict enumerates first. We do
        // not need true-LRU semantics for these caches -- the goal is bounded memory,
        // not optimal hit rate. The slice size matches the bump SqlitePathLifecycleStore
        // uses: knock 10% off so we don't re-evict on the very next insert.
        var overflow = dict.Count - targetSize + (targetSize / 10);
        if (overflow <= 0) return;
        var drops = 0;
        foreach (var kv in dict)
        {
            if (drops++ >= overflow) break;
            dict.TryRemove(kv.Key, out _);
        }
    }

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
        _dataDir = Path.GetDirectoryName(dbPath) ?? AppContext.BaseDirectory;
        // Directory.CreateDirectory deferred to EnsureInitialisedAsync: when
        // Identity.Enabled is false and ephemeral mode swaps the interface
        // bindings, the DI container still constructs this concrete (it's
        // injected into SqliteVecIdentityAnchorIndex) but no method on the
        // null-bound interface ever calls EnsureInitialisedAsync, so the
        // data directory is never created. Was unconditional in the ctor
        // before; this is the ephemeral-mode polish from the post-7.1 review.
        var fpDb = Path.Combine(_dataDir, "fingerprints.db");
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

            // Create the data directory lazily here, not in the ctor. Means
            // ephemeral mode (Identity.Enabled = false + NullFingerprintStore
            // bound to the interface) doesn't leave an empty fingerprint data
            // directory behind despite the concrete still being in the DI
            // container.
            Directory.CreateDirectory(_dataDir);

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                await pragma.ExecuteNonQueryAsync(ct);
            }

            await IdentitySchema.CreateCoreTablesAsync(conn, ct);
            await IdentitySchema.MigrateExistingTablesAsync(conn, ct);
            await IdentitySchema.BackfillRootCentroidsAsync(conn, ct);
            await IdentitySchema.SeedFingerprintModesAsync(conn, ct);
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
        if (string.IsNullOrEmpty(primarySignature)) return null;

        // L0 dict cache: this lookup is on the hot path for every request and the
        // primarySig->fingerprintId mapping is append-once / stable.
        if (_fingerprintIdByPrimarySig.TryGetValue(primarySignature, out var cachedId))
            return cachedId;

        var epochBefore = System.Threading.Volatile.Read(ref _fingerprintIdEpoch);

        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT fingerprint_id FROM fingerprint_keys WHERE primary_signature = @sig";
        cmd.Parameters.AddWithValue("@sig", primarySignature);
        var result = await cmd.ExecuteScalarAsync(ct);
        var fingerprintId = result as string;
        if (fingerprintId is not null
            && System.Threading.Volatile.Read(ref _fingerprintIdEpoch) == epochBefore)
        {
            _fingerprintIdByPrimarySig[primarySignature] = fingerprintId;
            if (_fingerprintIdByPrimarySig.Count > FingerprintIdCacheMaxEntries)
                EvictOldest(_fingerprintIdByPrimarySig, FingerprintIdCacheMaxEntries);
        }
        return fingerprintId;
    }

    public async Task<Fingerprint?> GetFingerprintAsync(string fingerprintId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return null;

        // L0 dict cache: fingerprint rows are wide and read repeatedly per request
        // (verdict gate, AI opinion, dashboard composer all reload them).
        if (_fingerprintById.TryGetValue(fingerprintId, out var cachedFp))
            return cachedFp;

        var epochBefore = System.Threading.Volatile.Read(ref _fingerprintEpoch);

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
                   display_name, display_name_updated_at,
                   root_centroid, root_centroid_at, root_source,
                   claim_status, verification_method, verified_at, trust_observations
              FROM fingerprints WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) return null;
        var fp = ReadFingerprint(reader);
        if (System.Threading.Volatile.Read(ref _fingerprintEpoch) == epochBefore)
        {
            _fingerprintById[fingerprintId] = fp;
            if (_fingerprintById.Count > FingerprintCacheMaxEntries)
                EvictOldest(_fingerprintById, FingerprintCacheMaxEntries);
        }
        return fp;
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
                    display_name, display_name_updated_at,
                    root_centroid, root_centroid_at, root_source,
                    claim_status, verification_method, verified_at, trust_observations
                ) VALUES (
                    @id, @centroid, @maturity, @weights, @members,
                    @observations, @corrections, @first_seen, @last_seen, @quality,
                    @origin, @inferred_type, @inferred_conf,
                    @inferred_changed, @cached_prob, @cached_band,
                    @cached_updated, @ambiguity,
                    @display_name, @display_name_updated,
                    @root_centroid, @root_at, @root_source,
                    @claim_status, @verification_method, @verified_at, @trust_observations
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
            // root_centroid is the reference drift is measured against. The matcher
            // is expected to seed it from the matched archetype's centroid (or the
            // seed centroid on the verifiedbot path); if anything in the allocation
            // flow forgets to set it, self-seed from the live centroid + source
            // "bootstrap" rather than leave it null. "Null root at request time is
            // a bug" -- catch it here so the dashboard never sees it.
            var rootCentroid = fp.RootCentroid ?? fp.Centroid;
            var rootAt = (fp.RootCentroidAt ?? fp.FirstSeen).ToString("O");
            var rootSource = fp.RootSource ?? "bootstrap";
            cmd.Parameters.AddWithValue("@root_centroid", FloatsToBlob(rootCentroid));
            cmd.Parameters.AddWithValue("@root_at", rootAt);
            cmd.Parameters.AddWithValue("@root_source", rootSource);
            // Trust state defaults to 'unverified' / NULL / NULL / 0 for a freshly
            // allocated fingerprint; verifier contributors call
            // UpdateClaimVerificationAsync after a successful verification to
            // flip claim_status -> 'verified' and stamp verified_at.
            cmd.Parameters.AddWithValue("@claim_status",
                string.IsNullOrEmpty(fp.ClaimStatus) ? "unverified" : fp.ClaimStatus);
            cmd.Parameters.AddWithValue("@verification_method",
                (object?)fp.VerificationMethod ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@verified_at",
                (object?)fp.VerifiedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@trust_observations", fp.TrustObservations);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Initial history row -- every root assignment must leave a trail so the
        // dashboard timeline can show the full evolution chain.
        await using (var hist = conn.CreateCommand())
        {
            hist.Transaction = tx;
            hist.CommandText = """
                INSERT INTO fingerprint_root_history
                    (fingerprint_id, root_centroid, root_source, member_count, set_at)
                VALUES (@id, @root_centroid, @root_source, 1, @set_at)
                """;
            hist.Parameters.AddWithValue("@id", fp.FingerprintId);
            hist.Parameters.AddWithValue("@root_centroid", FloatsToBlob(fp.RootCentroid ?? fp.Centroid));
            hist.Parameters.AddWithValue("@root_source", fp.RootSource ?? "bootstrap");
            hist.Parameters.AddWithValue("@set_at", (fp.RootCentroidAt ?? fp.FirstSeen).ToString("O"));
            await hist.ExecuteNonQueryAsync(ct);
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

        // Bug O fix: pre-populate the binding cache so the parallel HTTP/2 burst
        // (requests 2..N for the same primarySig) resolves the fingerprint id without
        // racing the WAL flush. We deliberately do NOT pre-populate _fingerprintById
        // with the input fp: the INSERT self-seeds root_centroid / root_centroid_at /
        // root_source when the input has nulls, and display_name_updated_at is coerced
        // to empty-string on default DateTime, so caching the input object would serve
        // a fingerprint shape that disagrees with the row on disk. Let the first
        // GetFingerprintAsync populate it from the canonical SELECT.
        _fingerprintIdByPrimarySig[primarySignature] = fp.FingerprintId;
        System.Threading.Interlocked.Increment(ref _fingerprintIdEpoch);
    }

    /// <summary>Insert or update fingerprint_keys binding a primary_signature to a fingerprint_id.</summary>
    public async Task UpsertKeyAsync(string primarySignature, string fingerprintId, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await UpsertKeyAsync(conn, null, primarySignature, fingerprintId, ct);

        // LFU façade: keep the dict authoritative even when rebinding an existing signature.
        _fingerprintIdByPrimarySig[primarySignature] = fingerprintId;
        System.Threading.Interlocked.Increment(ref _fingerprintIdEpoch);
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
        string fingerprintId, string displayName, DateTime updatedAt,
        CancellationToken ct = default,
        string source = "matcher")
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Single write gate. Read the current display name first so we can
        // decide whether this rename is a real transition that deserves a
        // history row, or a no-op that should not pollute the timeline view.
        // Skip the history row when:
        //   - new name is empty (the FingerprintAbsorptionService passes "" as
        //     a placeholder during absorption; that's not a name change)
        //   - new name equals old name (idempotent rewrites from the matcher's
        //     hysteresis path -- don't want N identical history rows per match)
        string? oldName = null;
        {
            await using var read = conn.CreateCommand();
            read.CommandText = "SELECT display_name FROM fingerprints WHERE fingerprint_id = @id";
            read.Parameters.AddWithValue("@id", fingerprintId);
            var raw = await read.ExecuteScalarAsync(ct);
            oldName = raw as string;
        }

        // Canonical-casing normalisation at the SINGLE write boundary into the
        // persistent fingerprint store. Whatever spelling a contributor or LLM
        // namer emits ("googlebot", "GOOGLEBOT", "Googlebot/2.1") gets folded
        // to the BotPatternLoader catalog's canonical casing before it lands
        // on the row. Stops casing-split parasites where the same identity
        // appeared as N rows because different writers raced to land different
        // strings in the same field. Unknown names (custom matcher labels,
        // fediverse instance suffixes not in the catalog) pass through as-is.
        var canonical = !string.IsNullOrEmpty(displayName)
            ? Definitions.BotPatterns.BotPatternLoader.Default.FindCanonicalCasing(displayName) ?? displayName
            : "";
        var newName = canonical;

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE fingerprints
                   SET display_name = @name,
                       display_name_updated_at = @ts
                 WHERE fingerprint_id = @id
                """;
            cmd.Parameters.AddWithValue("@name", newName);
            cmd.Parameters.AddWithValue("@ts", updatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@id", fingerprintId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        var isRealTransition =
            !string.IsNullOrEmpty(newName) &&
            !string.Equals(oldName ?? string.Empty, newName, StringComparison.Ordinal);

        if (isRealTransition)
        {
            await using var hist = conn.CreateCommand();
            hist.CommandText = """
                INSERT INTO fingerprint_name_history
                       (fingerprint_id, old_name, new_name, source, changed_at)
                VALUES (@id, @old, @new, @src, @ts)
                """;
            hist.Parameters.AddWithValue("@id", fingerprintId);
            hist.Parameters.AddWithValue("@old", string.IsNullOrEmpty(oldName) ? (object)DBNull.Value : oldName);
            hist.Parameters.AddWithValue("@new", newName);
            hist.Parameters.AddWithValue("@src", string.IsNullOrEmpty(source) ? "matcher" : source);
            hist.Parameters.AddWithValue("@ts", updatedAt.ToString("O"));
            await hist.ExecuteNonQueryAsync(ct);
        }

        InvalidateFingerprintCache(fingerprintId);
    }

    /// <summary>
    ///     Persistent trust state write. Updates <c>claim_status</c> /
    ///     <c>verification_method</c> / <c>verified_at</c> on the fingerprint
    ///     row so future requests can short-circuit re-verification while
    ///     within <c>TrustOptions.TrustCacheTtl</c>. Dict-authoritative LFU
    ///     façade: mutates the cached fingerprint first so the next L1 read
    ///     sees the new state immediately, then writes through to SQLite.
    ///     Per <c>feedback_write_behind_lfu_facade</c>.
    /// </summary>
    public async Task UpdateClaimVerificationAsync(
        string fingerprintId,
        string claimStatus,
        string? verificationMethod,
        DateTime? verifiedAt,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;
        if (string.IsNullOrEmpty(claimStatus)) claimStatus = "unverified";

        // Dict-authoritative replace so the next L1 read sees the trust
        // transition without waiting for the SQL commit. Same pattern as
        // RecordVerdictAsync (cached_bot_probability / cached_risk_band).
        if (_fingerprintById.TryGetValue(fingerprintId, out var existing))
        {
            _fingerprintById[fingerprintId] = existing with
            {
                ClaimStatus = claimStatus,
                VerificationMethod = verificationMethod,
                VerifiedAt = verifiedAt,
            };
        }

        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE fingerprints
               SET claim_status        = @claim_status,
                   verification_method = @verification_method,
                   verified_at         = @verified_at
             WHERE fingerprint_id = @id
            """;
        cmd.Parameters.AddWithValue("@claim_status", claimStatus);
        cmd.Parameters.AddWithValue("@verification_method",
            (object?)verificationMethod ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@verified_at",
            (object?)verifiedAt?.ToString("O") ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     Counts how many fingerprints already hold the given display name. Used by the
    ///     matcher to enforce the "same name = same fingerprint" rule at allocation time:
    ///     a non-zero count means a different fingerprint owns the name and the new one
    ///     must take a distinguished form. Empty / null name returns 0 -- those names
    ///     don't get persisted in the first place, so they never collide.
    /// </summary>
    public async Task<int> CountByDisplayNameAsync(string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(displayName)) return 0;
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fingerprints WHERE display_name = @name";
        cmd.Parameters.AddWithValue("@name", displayName);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is long n ? (int)n : 0;
    }

    /// <summary>
    ///     Update the display name on whichever fingerprint <paramref name="primarySignature"/>
    ///     currently maps to. One-shot helper for downstream consumers (the LLM-result callback,
    ///     dashboard "rename" controls) that have a signature in hand but not a fingerprint id.
    ///     Idempotent; no-op when the signature isn't bound to any fingerprint.
    /// </summary>
    public async Task UpdateDisplayNameForSignatureAsync(
        string primarySignature, string displayName, DateTime updatedAt,
        CancellationToken ct = default,
        string source = "matcher")
    {
        if (string.IsNullOrEmpty(primarySignature)) return;
        var fingerprintId = await LookupFingerprintIdAsync(primarySignature, ct);
        if (fingerprintId is null) return;
        await UpdateDisplayNameAsync(fingerprintId, displayName, updatedAt, ct, source);
    }

    /// <summary>Append an unabsorbed observation row.</summary>
    public async Task RecordObservationAsync(
        string fingerprintId,
        float[] vector,
        string? uaFamily = null,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_observations (fingerprint_id, vector, observed_at, absorbed_at, ua_family)
            VALUES (@id, @vec, @ts, NULL, @ua);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        cmd.Parameters.AddWithValue("@vec", FloatsToBlob(vector));
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@ua", (object?)uaFamily ?? DBNull.Value);
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

        InvalidateFingerprintCache(fingerprintId);

        try
        {
            ObservationAppended?.Invoke(fingerprintId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ObservationAppended handler threw for {FingerprintId}", fingerprintId);
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
        InvalidateFingerprintCache(pass2FingerprintId);
        if (!string.IsNullOrEmpty(pass1FingerprintId))
            InvalidateFingerprintCache(pass1FingerprintId);
        // Pass-2 correction may rebind primarySig to pass2FingerprintId in the binding store.
        // Drop any cached binding so the next read re-resolves to the current truth.
        if (_fingerprintIdByPrimarySig.TryRemove(primarySignature, out _))
            System.Threading.Interlocked.Increment(ref _fingerprintIdEpoch);
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
        InvalidateFingerprintCache(fingerprintId);
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
                   f.inferred_client_type, o.ua_family
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
                InferredClientType = reader.GetString(9),
                UaFamily = reader.IsDBNull(10) ? null : reader.GetString(10)
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
                   display_name, display_name_updated_at,
                   root_centroid, root_centroid_at, root_source,
                   claim_status, verification_method, verified_at, trust_observations
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
                   display_name, display_name_updated_at,
                   root_centroid, root_centroid_at, root_source,
                   claim_status, verification_method, verified_at, trust_observations
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

        // LFU façade invalidation: drop the fingerprint row so the next read reloads
        // the row we just rewrote. Manual-operator AI button "force re-read" semantics.
        _fingerprintById.TryRemove(fingerprintId, out _);
    }

    /// <summary>
    ///     Request-path verdict write. EWMA-blends the incoming bot probability with the
    ///     fingerprint's existing cached value, writes through the in-memory dict so the
    ///     next L1 verdict lookup sees the new value immediately, and persists to SQLite
    ///     for restart-survival. First-ever write is a direct assignment so a brand-new
    ///     fingerprint's first detection lands its real probability, not an attenuated
    ///     blend against the default 0.0.
    /// </summary>
    public async Task RecordVerdictAsync(
        string fingerprintId,
        double botProbability,
        string? riskBand,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;

        var alpha = Math.Clamp(_engineOptions.VerdictEwmaAlpha, 0.0, 1.0);

        // Dict-authoritative write: update the in-memory fingerprint so the next L1
        // verdict lookup sees it immediately. Cold path: load from SQLite first.
        if (!_fingerprintById.TryGetValue(fingerprintId, out var existing))
        {
            existing = await GetFingerprintAsync(fingerprintId, ct);
            if (existing is null) return;
        }

        // First-ever write (CachedScoreUpdatedAt is null) is a direct assignment so a
        // brand-new fingerprint's first detection lands its real probability, not an
        // alpha-attenuated 0.3 * something.
        var blended = existing.CachedScoreUpdatedAt is null
            ? botProbability
            : existing.CachedBotProbability * (1.0 - alpha) + botProbability * alpha;

        var now = DateTime.UtcNow;
        var updated = existing with
        {
            CachedBotProbability = blended,
            CachedRiskBand       = riskBand ?? existing.CachedRiskBand,
            CachedScoreUpdatedAt = now
        };

        // Atomic replace in the dict. Source of truth on the hot read path; the SQL
        // write below is durability only. Order matters: even if SQLite throws, the
        // next L1 lookup hits the new value.
        _fingerprintById[fingerprintId] = updated;

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
        cmd.Parameters.AddWithValue("@prob", blended);
        cmd.Parameters.AddWithValue("@band", (object?)updated.CachedRiskBand ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ts", now.ToString("O"));
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<RootHistoryEntry>> GetRootHistoryAsync(
        string fingerprintId, int limit = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId))
            return Array.Empty<RootHistoryEntry>();
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, fingerprint_id, root_centroid, root_source, member_count,
                   set_at, superseded_at
              FROM fingerprint_root_history
             WHERE fingerprint_id = @id
             ORDER BY set_at DESC
             LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));
        var results = new List<RootHistoryEntry>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new RootHistoryEntry(
                Id: reader.GetInt64(0),
                FingerprintId: reader.GetString(1),
                RootCentroid: BlobToFloats((byte[])reader.GetValue(2)),
                RootSource: reader.GetString(3),
                MemberCount: reader.GetInt32(4),
                SetAt: DateTime.Parse(reader.GetString(5), null, System.Globalization.DateTimeStyles.RoundtripKind),
                SupersededAt: reader.IsDBNull(6)
                    ? null
                    : DateTime.Parse(reader.GetString(6), null, System.Globalization.DateTimeStyles.RoundtripKind)));
        }
        return results;
    }

    // SQLite's default SQLITE_MAX_VARIABLE_NUMBER is 999. An IN-clause built from
    // a cluster's member signatures unbounded would throw on a 1000-member
    // BotNetwork cluster, and the fire-and-forget hook in BotClusterService
    // swallows the exception silently. Chunk at 500 to stay well clear of the
    // cap; the round-trip cost on a hot store is negligible.
    private const int ReseatBatchSize = 500;

    public async Task ReseatRootCentroidsAsync(
        IReadOnlyCollection<ClusterRootUpdate> updates,
        int minMemberFingerprints = 2,
        CancellationToken ct = default)
    {
        if (updates.Count == 0) return;
        await EnsureInitialisedAsync(ct);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var now = DateTime.UtcNow.ToString("O");
        var totalClustersApplied = 0;
        var totalFingerprintsReseated = 0;
        var reseatedIds = new HashSet<string>(StringComparer.Ordinal);

        foreach (var update in updates)
        {
            if (update.MemberSignatures.Count == 0) continue;

            // Resolve cluster signatures -> unique fingerprint ids in batched queries.
            var fingerprintIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var batch in Chunked(update.MemberSignatures, ReseatBatchSize))
            {
                await using var lookup = conn.CreateCommand();
                lookup.Transaction = tx;
                var paramNames = new List<string>(batch.Count);
                for (var i = 0; i < batch.Count; i++)
                {
                    var n = "@s" + i;
                    paramNames.Add(n);
                    lookup.Parameters.AddWithValue(n, batch[i]);
                }
                lookup.CommandText =
                    $"SELECT DISTINCT fingerprint_id FROM fingerprint_keys WHERE primary_signature IN ({string.Join(",", paramNames)})";
                await using var rd = await lookup.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    fingerprintIds.Add(rd.GetString(0));
            }

            if (fingerprintIds.Count < minMemberFingerprints) continue;

            // Fetch member centroids -> compute mean (batched).
            var fingerprintIdList = fingerprintIds.ToList();
            var memberVectors = new List<float[]>(fingerprintIds.Count);
            foreach (var batch in Chunked(fingerprintIdList, ReseatBatchSize))
            {
                await using var fetch = conn.CreateCommand();
                fetch.Transaction = tx;
                var paramNames = new List<string>(batch.Count);
                for (var i = 0; i < batch.Count; i++)
                {
                    var n = "@f" + i;
                    paramNames.Add(n);
                    fetch.Parameters.AddWithValue(n, batch[i]);
                }
                fetch.CommandText =
                    $"SELECT centroid FROM fingerprints WHERE fingerprint_id IN ({string.Join(",", paramNames)})";
                await using var rd = await fetch.ExecuteReaderAsync(ct);
                while (await rd.ReadAsync(ct))
                    memberVectors.Add(BlobToFloats((byte[])rd.GetValue(0)));
            }

            if (memberVectors.Count < minMemberFingerprints) continue;
            var mean = MeanVector(memberVectors);
            if (mean is null) continue; // every fetched centroid had a wrong layout dim
            var meanBlob = FloatsToBlob(mean);
            var rootSource = "cluster:" + update.ClusterId;

            // Supersede the active history row for each member (batched same as
            // the resolve/fetch above -- single UPDATE with a 1000+ entry IN
            // clause would hit the SQLite param cap).
            foreach (var batch in Chunked(fingerprintIdList, ReseatBatchSize))
            {
                await using var supersede = conn.CreateCommand();
                supersede.Transaction = tx;
                var paramNames = new List<string>(batch.Count);
                for (var i = 0; i < batch.Count; i++)
                {
                    var n = "@f" + i;
                    paramNames.Add(n);
                    supersede.Parameters.AddWithValue(n, batch[i]);
                }
                supersede.Parameters.AddWithValue("@now", now);
                supersede.CommandText = $"""
                    UPDATE fingerprint_root_history
                       SET superseded_at = @now
                     WHERE fingerprint_id IN ({string.Join(",", paramNames)})
                       AND superseded_at IS NULL
                    """;
                await supersede.ExecuteNonQueryAsync(ct);
            }

            // Insert the new active history row + update the fingerprints row, per member.
            foreach (var id in fingerprintIds)
            {
                await using (var hist = conn.CreateCommand())
                {
                    hist.Transaction = tx;
                    hist.CommandText = """
                        INSERT INTO fingerprint_root_history
                            (fingerprint_id, root_centroid, root_source, member_count, set_at)
                        VALUES (@id, @centroid, @source, @members, @now)
                        """;
                    hist.Parameters.AddWithValue("@id", id);
                    hist.Parameters.AddWithValue("@centroid", meanBlob);
                    hist.Parameters.AddWithValue("@source", rootSource);
                    hist.Parameters.AddWithValue("@members", fingerprintIds.Count);
                    hist.Parameters.AddWithValue("@now", now);
                    await hist.ExecuteNonQueryAsync(ct);
                }

                await using (var upd = conn.CreateCommand())
                {
                    upd.Transaction = tx;
                    upd.CommandText = """
                        UPDATE fingerprints
                           SET root_centroid    = @centroid,
                               root_centroid_at = @now,
                               root_source      = @source
                         WHERE fingerprint_id = @id
                        """;
                    upd.Parameters.AddWithValue("@id", id);
                    upd.Parameters.AddWithValue("@centroid", meanBlob);
                    upd.Parameters.AddWithValue("@now", now);
                    upd.Parameters.AddWithValue("@source", rootSource);
                    await upd.ExecuteNonQueryAsync(ct);
                }
            }

            totalClustersApplied++;
            totalFingerprintsReseated += fingerprintIds.Count;
            foreach (var id in fingerprintIds) reseatedIds.Add(id);
        }

        await tx.CommitAsync(ct);

        foreach (var id in reseatedIds) InvalidateFingerprintCache(id);

        if (totalClustersApplied > 0)
            _logger.LogInformation(
                "Root reseat applied: {Clusters} clusters / {Fingerprints} fingerprints from {Total} cluster updates",
                totalClustersApplied, totalFingerprintsReseated, updates.Count);
    }

    /// <summary>
    ///     Yields <paramref name="source"/> in slices of up to <paramref name="size"/>.
    ///     Used to keep parameterised IN-clauses under SQLite's 999-parameter cap.
    ///     Materialises each chunk so the caller can index the same items twice
    ///     (parameter name + value) without iterating the source enumerable twice.
    /// </summary>
    private static IEnumerable<IReadOnlyList<T>> Chunked<T>(IReadOnlyCollection<T> source, int size)
    {
        if (source.Count == 0) yield break;
        if (source.Count <= size) { yield return source as IReadOnlyList<T> ?? source.ToList(); yield break; }
        var buffer = new List<T>(size);
        foreach (var item in source)
        {
            buffer.Add(item);
            if (buffer.Count == size)
            {
                yield return buffer;
                buffer = new List<T>(size);
            }
        }
        if (buffer.Count > 0) yield return buffer;
    }

    private static float[]? MeanVector(IReadOnlyList<float[]> vectors)
    {
        var dim = vectors[0].Length;
        var sum = new double[dim];
        var n = 0;
        foreach (var v in vectors)
        {
            // Tolerate stray dim mismatches (layout migrations etc.) by skipping the row.
            // Divisor counts CONFORMING vectors only -- counting all of them dilutes the
            // mean toward zero whenever any are skipped, which silently writes a
            // near-zero root centroid to every member fingerprint.
            if (v.Length != dim) continue;
            for (var i = 0; i < dim; i++) sum[i] += v[i];
            n++;
        }
        if (n == 0) return null;
        var mean = new float[dim];
        for (var i = 0; i < dim; i++) mean[i] = (float)(sum[i] / n);
        return mean;
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
            "fingerprint_root_history",
            // Must truncate before fingerprints -- name_history has a REFERENCES
            // fingerprints(fingerprint_id) FK and SQLite's BdfReplay reset path
            // returns SQLite Error 19 'FOREIGN KEY constraint failed' if the
            // parent rows go first.
            "fingerprint_name_history",
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

        _fingerprintIdByPrimarySig.Clear();
        _fingerprintById.Clear();
        System.Threading.Interlocked.Increment(ref _fingerprintIdEpoch);
        System.Threading.Interlocked.Increment(ref _fingerprintEpoch);

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
    public async Task UpdateRollupCentroidAsync(
        string fingerprintId, float[] newCentroid, int newMaturity, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId) || newCentroid.Length == 0) return;
        await EnsureInitialisedAsync(ct);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                UPDATE fingerprints
                   SET centroid          = @centroid,
                       centroid_maturity = @maturity
                 WHERE fingerprint_id = @id
                """;
            cmd.Parameters.AddWithValue("@centroid", FloatsToBlob(newCentroid));
            cmd.Parameters.AddWithValue("@maturity", newMaturity);
            cmd.Parameters.AddWithValue("@id", fingerprintId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        if (_vecAvailable)
        {
            // Same delete-then-insert dance AbsorbObservationAsync uses for vec0 —
            // keeps the KNN index in sync with the centroid blob in the SQL table.
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
        }

        await tx.CommitAsync(ct);
        InvalidateFingerprintCache(fingerprintId);
    }

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
        InvalidateFingerprintCache(fingerprintId);
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
        InvalidateFingerprintCache(fingerprintId);
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
    ///     Bulk transparent-LFU read for dashboard view rendering. For each signature:
    ///     check the two existing LFU dicts (_fingerprintIdByPrimarySig +
    ///     _fingerprintById); take the hits, batch the misses into one SQL roundtrip,
    ///     populate both dicts with what we loaded, and return signature -> current
    ///     display name. On a hot cache this never touches SQL.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetDisplayNamesBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures, CancellationToken ct = default)
    {
        var result = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (primarySignatures.Count == 0) return result;

        List<string>? missingSigs = null;

        foreach (var sig in primarySignatures)
        {
            if (string.IsNullOrEmpty(sig)) continue;
            if (result.ContainsKey(sig)) continue;

            if (_fingerprintIdByPrimarySig.TryGetValue(sig, out var fpId)
                && _fingerprintById.TryGetValue(fpId, out var fp))
            {
                result[sig] = string.IsNullOrEmpty(fp.DisplayName) ? null : fp.DisplayName;
                continue;
            }

            (missingSigs ??= new List<string>(primarySignatures.Count)).Add(sig);
        }

        if (missingSigs is null || missingSigs.Count == 0) return result;

        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sb = new System.Text.StringBuilder();
        sb.Append("""
            SELECT k.primary_signature, f.fingerprint_id, f.display_name
              FROM fingerprint_keys k
              JOIN fingerprints f ON f.fingerprint_id = k.fingerprint_id
             WHERE k.primary_signature IN (
            """);
        for (var i = 0; i < missingSigs.Count; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append('@').Append('p').Append(i);
        }
        sb.Append(')');

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();
        for (var i = 0; i < missingSigs.Count; i++)
            cmd.Parameters.AddWithValue($"@p{i}", missingSigs[i]);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var sig = reader.GetString(0);
            var fpId = reader.GetString(1);
            var name = reader.IsDBNull(2) ? null : reader.GetString(2);
            _fingerprintIdByPrimarySig[sig] = fpId;
            result[sig] = string.IsNullOrEmpty(name) ? null : name;
        }

        foreach (var sig in missingSigs)
            if (!result.ContainsKey(sig))
                result[sig] = null;

        return result;
    }

    /// <summary>
    ///     Direct DB read of the fingerprint name change history -- snapshot data,
    ///     not LFU-cached. Returned newest-first; bounded by <paramref name="limit"/>
    ///     to keep the timeline view's payload manageable on chatty fingerprints.
    /// </summary>
    public async Task<IReadOnlyList<DisplayNameChange>> GetDisplayNameHistoryAsync(
        string fingerprintId, int limit = 50, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return Array.Empty<DisplayNameChange>();
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT old_name, new_name, source, changed_at
              FROM fingerprint_name_history
             WHERE fingerprint_id = @id
             ORDER BY changed_at DESC, id DESC
             LIMIT @lim
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        cmd.Parameters.AddWithValue("@lim", limit);
        var list = new List<DisplayNameChange>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var old = reader.IsDBNull(0) ? null : reader.GetString(0);
            var @new = reader.GetString(1);
            var src = reader.GetString(2);
            var ts = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind);
            list.Add(new DisplayNameChange(old, @new, src, ts));
        }
        return list;
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
        BindArchetypeParams(cmd, archetype);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task InsertArchetypeIfMissingAsync(IdentityArchetype archetype, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // INSERT ... ON CONFLICT DO NOTHING preserves the existing row -- the
        // calibration tick's UpsertArchetypeAsync remains the ONLY path that
        // can mutate a refined centroid after first seeding.
        cmd.CommandText = """
            INSERT INTO identity_archetypes
                (archetype_id, name, description, centroid, dimension_mask, archetype_kind,
                 descendant_count, last_refined_at)
                VALUES (@id, @name, @desc, @centroid, @mask, @kind, @count, @ts)
                ON CONFLICT(archetype_id) DO NOTHING
            """;
        BindArchetypeParams(cmd, archetype);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    private static void BindArchetypeParams(SqliteCommand cmd, IdentityArchetype archetype)
    {
        cmd.Parameters.AddWithValue("@id", archetype.ArchetypeId);
        cmd.Parameters.AddWithValue("@name", archetype.Name);
        cmd.Parameters.AddWithValue("@desc", (object?)archetype.Description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@centroid", FloatsToBlob(archetype.Centroid));
        cmd.Parameters.AddWithValue("@mask", FloatsToBlob(archetype.DimensionMask));
        cmd.Parameters.AddWithValue("@kind", archetype.ArchetypeKind);
        cmd.Parameters.AddWithValue("@count", archetype.DescendantCount);
        cmd.Parameters.AddWithValue("@ts", archetype.LastRefinedAt.ToString("O"));
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
            : DateTime.Parse(reader.GetString(19), null, System.Globalization.DateTimeStyles.RoundtripKind),
        RootCentroid = reader.IsDBNull(20) ? null : BlobToFloats((byte[])reader.GetValue(20)),
        RootCentroidAt = reader.IsDBNull(21)
            ? null
            : DateTime.Parse(reader.GetString(21), null, System.Globalization.DateTimeStyles.RoundtripKind),
        RootSource = reader.IsDBNull(22) ? null : reader.GetString(22),
        // Trust state (gap #4). Older rows pre-migration default to
        // 'unverified' / NULL / NULL / 0 via the ALTER TABLE column defaults.
        ClaimStatus = reader.IsDBNull(23) ? "unverified" : reader.GetString(23),
        VerificationMethod = reader.IsDBNull(24) ? null : reader.GetString(24),
        VerifiedAt = reader.IsDBNull(25)
            ? null
            : DateTime.Parse(reader.GetString(25), null, System.Globalization.DateTimeStyles.RoundtripKind),
        TrustObservations = reader.IsDBNull(26) ? 0 : reader.GetInt32(26),
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

    /// <inheritdoc />
    public async Task<IReadOnlyList<NearestFingerprint>> GetNearestForSignatureAsync(
        string primarySignature, int k, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(primarySignature) || k <= 0 || !_vecAvailable)
            return Array.Empty<NearestFingerprint>();

        var selfId = await LookupFingerprintIdAsync(primarySignature, ct);
        if (string.IsNullOrEmpty(selfId)) return Array.Empty<NearestFingerprint>();

        var self = await GetFingerprintAsync(selfId, ct);
        if (self?.Centroid is not { Length: > 0 }) return Array.Empty<NearestFingerprint>();

        var hits = await SearchVecCentroidsAsync(self.Centroid, k + 1, ct);

        var matched = new List<NearestFingerprint>(k);
        foreach (var (id, distance) in hits)
        {
            if (string.Equals(id, selfId, StringComparison.Ordinal)) continue;
            var neighbour = await GetFingerprintAsync(id, ct);
            if (neighbour is null) continue;
            matched.Add(new NearestFingerprint(
                FingerprintId: id,
                DisplayName: neighbour.DisplayName,
                InferredClientType: neighbour.InferredClientType,
                Distance: distance));
            if (matched.Count >= k) break;
        }
        return matched;
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

