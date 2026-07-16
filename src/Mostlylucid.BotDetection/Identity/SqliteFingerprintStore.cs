using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Domains;
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
    private readonly IdentityVectorOptions _vectorOptions;

    /// <summary>
    ///     The identity vector options, shared with the browser-mode store so both write
    ///     paths sample observations with the same adaptive-forgetting policy. internal:
    ///     the mode store already holds a reference to this parent.
    /// </summary>
    internal IdentityVectorOptions VectorOptions => _vectorOptions;
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

    /// <summary>
    ///     Optional adaptive-trigger signal source. When non-null, every
    ///     successful <see cref="RecordObservationAsync"/> increments the
    ///     `obs.unabsorbed` counter so the calibration trigger can react to
    ///     observation pressure. DI registers the concrete
    ///     <see cref="Triggers.CalibrationSignalSource"/>; tests pass null.
    /// </summary>
    private readonly Triggers.IAdaptiveTriggerSignalSource? _triggerSignals;

    // ----------------------------------------------------------------------
    // Single-writer drain channel for all async fingerprint store writes.
    // Covers three write kinds:
    //   InducedNameWrite  - matcher-side induced-name slot (per-request high-freq)
    //   LlmNameWrite      - LLM-evaluated name slot (drift-triggered)
    //   AbsorbWrite       - centroid absorption (debounced, N fingerprints in
    //                       parallel without single-writer routing would race on
    //                       the SQLite write lock: overview-ratified 4th site fix)
    //
    // Per feedback_write_behind_lfu_facade + §4a constraint 2: none of these
    // write kinds may open a concurrent SQLite write connection. The dict
    // (_fingerprintById) is source of truth on the hot read path; this channel
    // funnels all three through a single drainer task.
    //
    // Operator GivenName edits stay synchronous: they're rare and durability
    // matters before the endpoint returns 200.
    // ----------------------------------------------------------------------
    private abstract record NameWrite(string FingerprintId, DateTime At);
    private sealed record InducedNameWrite(
        string FingerprintId, string? OldName, string NewName, DateTime At, string? SignalSnapshotJson)
        : NameWrite(FingerprintId, At);
    private sealed record LlmNameWrite(
        string FingerprintId, string? OldName, string NewName, string? Description, DateTime At)
        : NameWrite(FingerprintId, At);
    private sealed record AbsorbWrite(
        string FingerprintId,
        long ObservationId,
        float[] NewCentroid,
        int NewMaturity,
        float[] NewWeights,
        string? NewInferredClientType,
        double NewInferredTypeConfidence,
        bool InferredTypeChanged,
        DateTime At) : NameWrite(FingerprintId, At);
    // Hot-path identity-verdict persist. The EWMA blend happens in-memory (dict-first) on the
    // caller thread; only this durability write rides the shared name drainer, so per-request
    // verdict recording never opens a connection on the request path.
    private sealed record VerdictWrite(
        string FingerprintId, double Probability, string? RiskBand, DateTime At)
        : NameWrite(FingerprintId, At);

    private const int NameWriteQueueCapacity = 4096;
    private readonly Channel<NameWrite> _nameWriteChannel =
        Channel.CreateBounded<NameWrite>(new BoundedChannelOptions(NameWriteQueueCapacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    private Task? _nameDrainerTask;
    private readonly object _nameDrainerInitLock = new();

    // Test instrumentation: counts how many absorb writes the drainer has committed.
    // internal so tests in Mostlylucid.BotDetection.Test can assert the drain count
    // without leaking to callers.
    internal int AbsorbWriteCount;

    // Test instrumentation: counts observations that adaptive sampling summarised
    // (count + maturity advanced, no detail row written). internal so the flood
    // tests can assert confirmatory observations were forgotten, not persisted.
    internal long SummarisedObservationCount;

    public SqliteFingerprintStore(
        ILogger<SqliteFingerprintStore> logger,
        IOptions<BotDetectionOptions> options,
        IdentityVectorLayout layout,
        Triggers.IAdaptiveTriggerSignalSource? triggerSignals = null)
    {
        _logger = logger;
        _layout = layout;
        _engineOptions = options.Value.Identity.Engine;
        _vectorOptions = options.Value.Identity.Vector;
        _triggerSignals = triggerSignals;
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
            Data.StoreDbDirectory.EnsureExists(_dataDir);

            // Layout migration BEFORE the schema opens: if a prior fingerprints.db exists
            // at a different vector layout, its centroids/observations are the wrong
            // dimension and EnsureLayoutRowAsync below would refuse to start. Centroids are
            // re-learnable from traffic, so we wipe the db and let it re-seed at the new
            // layout -- the "fresh allocation" the layout versioning promises, made real.
            // A layout bump is a rare deploy-time event, so a one-time warm-up restage is
            // the right trade against a hard startup crash.
            await MigrateStaleLayoutAsync(ct);

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

    /// <summary>
    ///     Wipes <c>fingerprints.db</c> when the stored vector layout is incompatible with the
    ///     running one (version or dimension changed), so the schema re-creates and re-seeds at
    ///     the new layout instead of hard-failing in <see cref="EnsureLayoutRowAsync"/>. The
    ///     centroids, observations, archetypes and dimension weights are all layout-dimensioned
    ///     and re-learnable from live traffic, so a bump is a one-time warm-up restage.
    /// </summary>
    private async Task MigrateStaleLayoutAsync(CancellationToken ct)
    {
        var dbPath = Path.Combine(_dataDir, "fingerprints.db");
        if (!File.Exists(dbPath)) return; // fresh install -> schema creates at the current layout

        if (!await IsStoredLayoutIncompatibleAsync(ct)) return;

        // Release pooled handles so the file delete succeeds (matters on Windows).
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = dbPath + suffix;
            try
            {
                if (File.Exists(p)) File.Delete(p);
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Layout migration: could not delete {Path}", p);
            }
        }
    }

    private async Task<bool> IsStoredLayoutIncompatibleAsync(CancellationToken ct)
    {
        try
        {
            await using var probe = new SqliteConnection(_connectionString);
            await probe.OpenAsync(ct);
            await using var cmd = probe.CreateCommand();
            cmd.CommandText = "SELECT version, dimension FROM identity_vector_layout WHERE id = 1";
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            if (!await reader.ReadAsync(ct))
                return false; // table present but no row -> EnsureLayoutRowAsync inserts cleanly

            var storedVersion = reader.GetInt32(0);
            var storedDim = reader.GetInt32(1);
            if (storedVersion == _layout.Version && storedDim == _layout.Dimension)
                return false; // compatible

            _logger.LogWarning(
                "Identity vector layout changed (stored v{StoredV}/dim{StoredD} -> running "
                + "v{RunV}/dim{RunD}); wiping fingerprints.db and re-seeding. Learned centroids "
                + "rebuild from traffic.",
                storedVersion, storedDim, _layout.Version, _layout.Dimension);
            return true;
        }
        catch (SqliteException)
        {
            // No readable identity_vector_layout (pre-versioning / foreign / corrupt db). We
            // cannot verify compatibility and the running schema guard would refuse to start,
            // so wipe to recover -- the data is re-learnable.
            _logger.LogWarning(
                "fingerprints.db has no readable identity_vector_layout; wiping and re-seeding "
                + "at layout v{RunV}.", _layout.Version);
            return true;
        }
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
                   induced_name, induced_name_updated_at,
                   llm_name, llm_evaluated_at, llm_description,
                   given_name, given_name_updated_at, given_name_operator_id,
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
                    induced_name, induced_name_updated_at,
                    llm_name, llm_evaluated_at, llm_description,
                    given_name, given_name_updated_at, given_name_operator_id,
                    root_centroid, root_centroid_at, root_source,
                    claim_status, verification_method, verified_at, trust_observations
                ) VALUES (
                    @id, @centroid, @maturity, @weights, @members,
                    @observations, @corrections, @first_seen, @last_seen, @quality,
                    @origin, @inferred_type, @inferred_conf,
                    @inferred_changed, @cached_prob, @cached_band,
                    @cached_updated, @ambiguity,
                    @induced_name, @induced_name_updated,
                    @llm_name, @llm_evaluated_at, @llm_description,
                    @given_name, @given_name_updated, @given_name_operator,
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
            // Never persist the matcher's supplied band verbatim (it may be a
            // hardcoded band from a contributor). When a verdict exists (score
            // timestamp set) derive the band from THIS row's probability so the
            // allocation is born consistent; otherwise leave it null (no verdict
            // yet). Single source of truth — see DeriveConsistentBand.
            cmd.Parameters.AddWithValue("@cached_band",
                fp.CachedScoreUpdatedAt is null
                    ? DBNull.Value
                    : DeriveConsistentBand(fp.CachedBotProbability, fp.InferredTypeConfidence));
            cmd.Parameters.AddWithValue("@cached_updated",
                (object?)fp.CachedScoreUpdatedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ambiguity", fp.AmbiguityPersistence);
            // Contract gate at the row's initial allocation. The matcher seeds the
            // InducedName on the brand-new fingerprint record from FingerprintNameComposer
            // (verifiedbot path) or persistedInducedName (new-allocation path); a banned
            // shape from either path must never land on disk. Empty string passes through
            // so the no-name-yet allocation case still works. LLM and Given slots are
            // null at allocation; the LLM coordinator and operator editor populate them.
            cmd.Parameters.AddWithValue(
                "@induced_name",
                NormaliseBannedShape(fp.InducedName, fp.FingerprintId, primarySignature));
            cmd.Parameters.AddWithValue("@induced_name_updated",
                fp.InducedNameUpdatedAt is { } iAt ? iAt.ToString("O") : "");
            cmd.Parameters.AddWithValue("@llm_name", (object?)fp.LlmName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@llm_evaluated_at",
                (object?)fp.LlmEvaluatedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@llm_description", (object?)fp.LlmDescription ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@given_name", (object?)fp.GivenName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@given_name_updated",
                (object?)fp.GivenNameUpdatedAt?.ToString("O") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@given_name_operator",
                (object?)fp.GivenNameOperatorId ?? DBNull.Value);
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
        // root_source when the input has nulls, and induced_name_updated_at is coerced
        // to empty-string on a null DateTime, so caching the input object would serve
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
    ///     Matcher writeback for <c>InducedName</c>. Per <c>feedback_write_behind_lfu_facade</c>
    ///     this is per-request high-frequency — SQLite cannot take a synchronous DB write
    ///     here. The dict (<c>_fingerprintById</c>) is updated synchronously so the next
    ///     L1 read sees the new value; the DB UPDATE + history INSERT happens off-path
    ///     via <see cref="_nameWriteChannel"/>'s drainer. No-op when the slot value is
    ///     unchanged (re-confirmations from the matcher's hysteresis path must not tick
    ///     <c>InducedNameUpdatedAt</c> — see spec §4 / NS7).
    /// </summary>
    public Task UpdateInducedNameAsync(
        string fingerprintId,
        string inducedName,
        DateTime updatedAt,
        CancellationToken ct,
        string? signalSnapshotJson = null)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return Task.CompletedTask;

        // Contract gate at the write boundary. Mirrors the pre-split behaviour:
        // banned shapes get normalised to the priority-4 Unknown <hex> fallback
        // before they reach the dict or the durable tier.
        var name = NormaliseBannedShape(inducedName, fingerprintId);

        // Canonical-casing normalisation at the single write boundary into the
        // persistent fingerprint store. Whatever spelling a contributor or LLM
        // namer emits ("googlebot", "GOOGLEBOT", "Googlebot/2.1") gets folded
        // to the BotPatternLoader catalog's canonical casing before it lands
        // on the row. Stops casing-split parasites where the same identity
        // appeared as N rows because different writers raced to land different
        // strings in the same field.
        if (!string.IsNullOrEmpty(name))
            name = Definitions.BotPatterns.BotPatternLoader.Default.FindCanonicalCasing(name) ?? name;

        if (!_fingerprintById.TryGetValue(fingerprintId, out var fp))
        {
            // Cold miss: nothing to merge into. The matcher always allocates +
            // GetFingerprintAsync's the row before driving recompose, so this is
            // a defensive return. Don't enqueue a write for a row we have no
            // dict view of (drainer would have to re-read the prior name from DB
            // for the history row, defeating the write-behind point).
            return Task.CompletedTask;
        }

        // Idempotency: no-op when the new name equals the existing slot value.
        // Avoids trigger-spam — InducedNameUpdatedAt ticks only on real transitions.
        if (string.Equals(fp.InducedName, name, StringComparison.Ordinal))
            return Task.CompletedTask;

        var prior = fp.InducedName;
        var updated = fp with { InducedName = name, InducedNameUpdatedAt = updatedAt };
        _fingerprintById[fingerprintId] = updated;

        EnsureNameDrainerStarted();
        _nameWriteChannel.Writer.TryWrite(new InducedNameWrite(
            fingerprintId, prior, name, updatedAt, signalSnapshotJson));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     LLM-namer writeback for <c>LlmName</c> + <c>LlmDescription</c>. Medium-frequency
    ///     (drift-triggered, bounded concurrency) — same write-behind LFU façade as
    ///     <see cref="UpdateInducedNameAsync"/>. <c>LlmEvaluatedAt</c> ticks on every
    ///     successful LLM pass per spec §4 so the picker can de-prioritise just-evaluated
    ///     rows even when the LLM returned the same name.
    /// </summary>
    public Task UpdateLlmNameAsync(
        string fingerprintId,
        string llmName,
        string? description,
        DateTime evaluatedAt,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return Task.CompletedTask;

        var name = NormaliseBannedShape(llmName, fingerprintId);
        if (!string.IsNullOrEmpty(name))
            name = Definitions.BotPatterns.BotPatternLoader.Default.FindCanonicalCasing(name) ?? name;

        if (!_fingerprintById.TryGetValue(fingerprintId, out var fp))
            return Task.CompletedTask;

        var prior = fp.LlmName;
        var updated = fp with
        {
            LlmName = name,
            LlmDescription = description,
            LlmEvaluatedAt = evaluatedAt,
        };
        _fingerprintById[fingerprintId] = updated;

        EnsureNameDrainerStarted();
        _nameWriteChannel.Writer.TryWrite(new LlmNameWrite(
            fingerprintId, prior, name, description, evaluatedAt));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     Operator-edit writeback for <c>GivenName</c>. Low-frequency human-rate write
    ///     where durability matters before the endpoint returns 200 — synchronous LFU+DB
    ///     write inside the request handler is fine here per spec §4a constraint 2. A
    ///     null / empty <paramref name="givenName"/> clears the pin so the resolver falls
    ///     back to <c>LlmName</c> / <c>InducedName</c>.
    /// </summary>
    public async Task UpdateGivenNameAsync(
        string fingerprintId,
        string? givenName,
        string operatorId,
        DateTime updatedAt,
        CancellationToken ct)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;

        var trimmed = string.IsNullOrWhiteSpace(givenName) ? null : givenName!.Trim();

        // Contract gate still fires on operator input — banned shapes from the
        // editor get normalised the same as matcher / LLM writes.
        if (trimmed is not null)
        {
            var normalised = NormaliseBannedShape(trimmed, fingerprintId);
            trimmed = string.IsNullOrEmpty(normalised) ? null : normalised;
        }

        // Dict-authoritative replace so the next L1 read sees the operator pin
        // without waiting for the SQL commit. Matches RecordVerdictAsync /
        // UpdateClaimVerificationAsync patterns elsewhere in this store.
        string? prior = null;
        if (_fingerprintById.TryGetValue(fingerprintId, out var fp))
        {
            prior = fp.GivenName;
            _fingerprintById[fingerprintId] = fp with
            {
                GivenName = trimmed,
                GivenNameUpdatedAt = updatedAt,
                GivenNameOperatorId = operatorId,
            };
        }

        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                UPDATE fingerprints
                   SET given_name              = @name,
                       given_name_updated_at   = @ts,
                       given_name_operator_id  = @op
                 WHERE fingerprint_id = @id
                """;
            cmd.Parameters.AddWithValue("@name", (object?)trimmed ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ts", updatedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@op", (object?)operatorId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@id", fingerprintId);
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Always record an operator edit in history — clears (trimmed == null)
        // are audit-relevant transitions even when no display change results.
        var isRealTransition = !string.Equals(prior ?? string.Empty, trimmed ?? string.Empty, StringComparison.Ordinal);
        if (isRealTransition)
        {
            await using var hist = conn.CreateCommand();
            hist.CommandText = """
                INSERT INTO fingerprint_name_history
                       (fingerprint_id, old_name, new_name, source, name_kind, operator_id, changed_at)
                VALUES (@id, @old, @new, 'operator', 'given', @op, @ts)
                """;
            hist.Parameters.AddWithValue("@id", fingerprintId);
            hist.Parameters.AddWithValue("@old", string.IsNullOrEmpty(prior) ? (object)DBNull.Value : prior);
            hist.Parameters.AddWithValue("@new", (object?)trimmed ?? DBNull.Value);
            hist.Parameters.AddWithValue("@op", (object?)operatorId ?? DBNull.Value);
            hist.Parameters.AddWithValue("@ts", updatedAt.ToString("O"));
            await hist.ExecuteNonQueryAsync(ct);
        }
    }

    /// <summary>
    ///     Idempotent drainer-task start. Lazy on first write so tests that
    ///     instantiate the store but never write a name don't pay for a
    ///     long-running task. Lock-guarded because multiple matcher threads
    ///     can race the first <c>UpdateInducedNameAsync</c>.
    /// </summary>
    private void EnsureNameDrainerStarted()
    {
        if (_nameDrainerTask is not null) return;
        lock (_nameDrainerInitLock)
        {
            _nameDrainerTask ??= Task.Run(DrainNameWritesAsync);
        }
    }

    /// <summary>
    ///     Background drainer: pulls queued <see cref="NameWrite"/> entries off the
    ///     channel and persists them to SQLite. Each write does its own
    ///     UPDATE + history INSERT inside the same connection — no transaction
    ///     because (a) write-behind tolerates partial failure (the dict is the
    ///     source of truth, durability is best-effort retry) and (b) batching
    ///     N independent fingerprint updates in one transaction would serialise
    ///     unrelated rows behind one another.
    /// </summary>
    private async Task DrainNameWritesAsync()
    {
        var reader = _nameWriteChannel.Reader;
        try
        {
            while (await reader.WaitToReadAsync().ConfigureAwait(false))
            {
                while (reader.TryRead(out var write))
                {
                    try
                    {
                        await PersistNameWriteAsync(write).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex,
                            "Write-behind name drainer failed for fingerprint {Id}; dict remains authoritative",
                            write.FingerprintId);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Write-behind name drainer crashed; in-memory dict still consistent");
        }
    }

    private async Task PersistNameWriteAsync(NameWrite write)
    {
        await EnsureInitialisedAsync(CancellationToken.None).ConfigureAwait(false);

        // AbsorbWrite needs a vec-capable connection and its own transaction;
        // dispatch before opening the plain name-write connection below.
        if (write is AbsorbWrite absorb)
        {
            await PersistAbsorbWriteAsync(absorb).ConfigureAwait(false);
            return;
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync().ConfigureAwait(false);

        switch (write)
        {
            case VerdictWrite verdict:
            {
                await using var cmd = conn.CreateCommand();
                cmd.CommandText = """
                    UPDATE fingerprints
                       SET cached_bot_probability  = @prob,
                           cached_risk_band        = @band,
                           cached_score_updated_at = @ts
                     WHERE fingerprint_id = @id
                    """;
                cmd.Parameters.AddWithValue("@prob", verdict.Probability);
                cmd.Parameters.AddWithValue("@band", (object?)verdict.RiskBand ?? DBNull.Value);
                cmd.Parameters.AddWithValue("@ts", verdict.At.ToString("O"));
                cmd.Parameters.AddWithValue("@id", verdict.FingerprintId);
                await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                break;
            }
            case InducedNameWrite ind:
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        UPDATE fingerprints
                           SET induced_name            = @name,
                               induced_name_updated_at = @ts
                         WHERE fingerprint_id = @id
                        """;
                    cmd.Parameters.AddWithValue("@name", (object?)ind.NewName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ts", ind.At.ToString("O"));
                    cmd.Parameters.AddWithValue("@id", ind.FingerprintId);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(ind.NewName)
                    && !string.Equals(ind.OldName ?? string.Empty, ind.NewName, StringComparison.Ordinal))
                {
                    await using var hist = conn.CreateCommand();
                    hist.CommandText = """
                        INSERT INTO fingerprint_name_history
                               (fingerprint_id, old_name, new_name, source, name_kind, changed_at, signal_snapshot_json)
                        VALUES (@id, @old, @new, 'matcher', 'induced', @ts, @snap)
                        """;
                    hist.Parameters.AddWithValue("@id", ind.FingerprintId);
                    hist.Parameters.AddWithValue("@old", string.IsNullOrEmpty(ind.OldName) ? (object)DBNull.Value : ind.OldName);
                    hist.Parameters.AddWithValue("@new", ind.NewName);
                    hist.Parameters.AddWithValue("@ts", ind.At.ToString("O"));
                    hist.Parameters.AddWithValue("@snap", (object?)ind.SignalSnapshotJson ?? DBNull.Value);
                    await hist.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                break;
            }
            case LlmNameWrite llm:
            {
                await using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = """
                        UPDATE fingerprints
                           SET llm_name         = @name,
                               llm_description  = @desc,
                               llm_evaluated_at = @ts
                         WHERE fingerprint_id = @id
                        """;
                    cmd.Parameters.AddWithValue("@name", (object?)llm.NewName ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@desc", (object?)llm.Description ?? DBNull.Value);
                    cmd.Parameters.AddWithValue("@ts", llm.At.ToString("O"));
                    cmd.Parameters.AddWithValue("@id", llm.FingerprintId);
                    await cmd.ExecuteNonQueryAsync().ConfigureAwait(false);
                }

                if (!string.IsNullOrEmpty(llm.NewName)
                    && !string.Equals(llm.OldName ?? string.Empty, llm.NewName, StringComparison.Ordinal))
                {
                    await using var hist = conn.CreateCommand();
                    hist.CommandText = """
                        INSERT INTO fingerprint_name_history
                               (fingerprint_id, old_name, new_name, source, name_kind, changed_at)
                        VALUES (@id, @old, @new, 'llm', 'llm', @ts)
                        """;
                    hist.Parameters.AddWithValue("@id", llm.FingerprintId);
                    hist.Parameters.AddWithValue("@old", string.IsNullOrEmpty(llm.OldName) ? (object)DBNull.Value : llm.OldName);
                    hist.Parameters.AddWithValue("@new", llm.NewName);
                    hist.Parameters.AddWithValue("@ts", llm.At.ToString("O"));
                    await hist.ExecuteNonQueryAsync().ConfigureAwait(false);
                }
                break;
            }
        }
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
    ///     Counts how many fingerprints already hold the given <c>induced_name</c>. Used by
    ///     the matcher to enforce the "same name = same fingerprint" rule at allocation
    ///     time: a non-zero count means a different fingerprint already projected to this
    ///     induced name and the new one must take a distinguished form. The collision
    ///     check is over the matcher-owned slot only — operator pins and LLM names are
    ///     not part of the matcher's identity contract. Empty / null name returns 0.
    /// </summary>
    public async Task<int> CountByDisplayNameAsync(string displayName, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(displayName)) return 0;
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fingerprints WHERE induced_name = @name";
        cmd.Parameters.AddWithValue("@name", displayName);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is long n ? (int)n : 0;
    }

    public async Task<int> CountByDisplayNameExcludingFingerprintAsync(
        string displayName, string excludedFingerprintId, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(displayName)) return 0;
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText =
            "SELECT COUNT(*) FROM fingerprints WHERE induced_name = @name AND fingerprint_id != @fp";
        cmd.Parameters.AddWithValue("@name", displayName);
        cmd.Parameters.AddWithValue("@fp", excludedFingerprintId ?? string.Empty);
        var raw = await cmd.ExecuteScalarAsync(ct);
        return raw is long n ? (int)n : 0;
    }

    /// <summary>
    ///     Shared name contract gate. Every write path that lands a value in any of the
    ///     three name slots (<c>induced_name</c> / <c>llm_name</c> / <c>given_name</c>)
    ///     funnels through this helper so a banned shape can never reach the row no
    ///     matter which entry point fired -- T24 staging found rows like
    ///     <c>"Chrome Desktop (missing client hints)"</c> persisted by paths that
    ///     bypassed the original single-method gate. Empty / null values are passthrough:
    ///     callers (e.g. the absorption service clearing a stale name after archetype
    ///     flip, the operator editor clearing a pin) intentionally write empty-string to
    ///     reset the field, and an empty string is not a banned-shape rejection -- it's
    ///     an explicit clear.
    /// </summary>
    private string NormaliseBannedShape(string? displayName, string fingerprintId, string? primarySignature = null)
    {
        if (string.IsNullOrEmpty(displayName)) return displayName ?? string.Empty;
        if (Services.FingerprintNameComposerContract.IsAllowedShape(displayName))
            return displayName;
        System.Threading.Interlocked.Increment(ref _bannedShapeRejections);
        return BuildUnknownFallback(fingerprintId, primarySignature ?? string.Empty);
    }

    private long _bannedShapeRejections;

    /// <inheritdoc />
    public long BannedShapeRejectionsCount => System.Threading.Interlocked.Read(ref _bannedShapeRejections);

    /// <summary>
    ///     Produce the priority-4 <c>Unknown &lt;hex&gt;</c> fallback for a banned-shape
    ///     rejection. Prefers the fingerprint id's first 8 hex chars so the rendered name
    ///     matches the existing identifier shape; falls back to the signature's first 8
    ///     chars if the fingerprint id is too short (defensive — shouldn't happen in
    ///     practice because the matcher always allocates a long id).
    /// </summary>
    private static string BuildUnknownFallback(string fingerprintId, string primarySignature)
    {
        string prefix;
        if (!string.IsNullOrEmpty(fingerprintId) && fingerprintId.Length >= 8)
            prefix = fingerprintId[..8];
        else if (!string.IsNullOrEmpty(primarySignature) && primarySignature.Length >= 8)
            prefix = primarySignature[..8];
        else
            prefix = "00000000";
        return $"Unknown {prefix}";
    }

    /// <summary>Append an unabsorbed observation row.</summary>
    /// <remarks>
    ///     Adaptive forgetting (<see cref="IdentityVectorOptions.AdaptiveObservationSampling"/>):
    ///     a confirmatory observation on an already-matured fingerprint is <i>summarised</i>
    ///     (count + centroid maturity advance, no detail row, no absorber wake) so the identity
    ///     store grows with behavioural novelty, not request volume. Novel observations and
    ///     observations on still-maturing fingerprints keep a full detail row exactly as before.
    /// </remarks>
    public async Task RecordObservationAsync(
        RequestScope scope,
        string fingerprintId,
        float[] vector,
        string? uaFamily = null,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);

        if (!await ShouldPersistObservationDetailAsync(fingerprintId, vector, ct))
        {
            await SummariseObservationAsync(fingerprintId, ct);
            return;
        }

        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO fingerprint_observations (fingerprint_id, vector, observed_at, absorbed_at, ua_family, domain, host)
            VALUES (@id, @vec, @ts, NULL, @ua, @domain, @host);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@id", fingerprintId);
        cmd.Parameters.AddWithValue("@vec", FloatsToBlob(vector));
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@ua", (object?)uaFamily ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@domain", (object?)scope.Domain ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@host", (object?)scope.Host ?? DBNull.Value);
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

        // Adaptive trigger signal: one obs arrived. Cast-to-CalibrationSignalSource
        // would be tighter, but the interface keeps this seam testable with the
        // null source and a future per-host signal source.
        if (_triggerSignals is Triggers.CalibrationSignalSource calSignals)
            calSignals.OnObservation();

        try
        {
            ObservationAppended?.Invoke(fingerprintId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ObservationAppended handler threw for {FingerprintId}", fingerprintId);
        }
    }

    /// <summary>
    ///     Adaptive-forgetting decision: does this observation earn a detail row? True for
    ///     novel observations, for every observation while a fingerprint is still maturing
    ///     (the identity-building phase, below <see cref="IdentityVectorOptions.AbsorptionMaturityThreshold"/>),
    ///     and always when sampling is disabled or the fingerprint / centroid is not yet
    ///     comparable. False only for a confirmatory observation on a matured fingerprint.
    /// </summary>
    private async Task<bool> ShouldPersistObservationDetailAsync(string fingerprintId, float[] vector, CancellationToken ct)
    {
        if (!_vectorOptions.AdaptiveObservationSampling)
            return true;

        // L0-cached read; the matcher just loaded this fingerprint, so it is warm.
        var fp = await GetFingerprintAsync(fingerprintId, ct);
        if (fp is null)
            return true; // brand-new fingerprint: keep detail (bootstrap).

        // Still learning the shape: every observation is identity-building, keep it.
        if (fp.CentroidMaturity < _vectorOptions.AbsorptionMaturityThreshold)
            return true;

        // Layout mismatch (versioned relayout, degenerate centroid): cannot judge
        // novelty, so keep detail rather than risk forgetting a real change.
        if (fp.Centroid.Length != vector.Length || vector.Length == 0)
            return true;

        // Novelty = distance from the established centroid = "does this change the score".
        // Cosine treats the composed vectors as L2-normalised (dot product); clamp guards a
        // slightly denormalised centroid from producing an out-of-range novelty.
        var novelty = Math.Clamp(1.0 - BruteForceIdentityAnchorIndex.Cosine(vector, fp.Centroid), 0.0, 2.0);
        return novelty >= _vectorOptions.ObservationNoveltyKeepThreshold;
    }

    /// <summary>
    ///     Summarise a confirmatory observation: advance the aggregate counters
    ///     (observation_count for crossing notifications, last_seen for recency) WITHOUT
    ///     writing a detail row, a vec row, or waking the absorber. Critically it must NOT
    ///     touch the centroid or centroid_maturity: the absorber is the sole owner of those,
    ///     and a second writer here desyncs the maturity-weighted fold (a summarised bump
    ///     followed by a real fold corrupts the centroid). centroid_maturity therefore counts
    ///     folded (novel) observations only, which is also the more correct notion of
    ///     confidence: confirmatory repetitions add no new information. This is the "still logs
    ///     a summarised entry for the unimportant ones" half of adaptive forgetting.
    /// </summary>
    private async Task SummariseObservationAsync(string fingerprintId, CancellationToken ct)
    {
        // Plain connection: the summary path never touches vec0, and skipping the
        // extension load keeps a confirmatory-observation flood cheap.
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
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

        InvalidateFingerprintCache(fingerprintId);
        System.Threading.Interlocked.Increment(ref SummarisedObservationCount);
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
    ///     Enqueues an absorption write to the single-writer drain channel.
    ///     The actual SQLite transaction (centroid + obs mark + vec0 updates)
    ///     runs inside <see cref="PersistAbsorbWriteAsync"/> on the drainer
    ///     task, serializing concurrent debounced absorptions through one
    ///     write connection instead of racing on the WAL lock.
    ///
    ///     Fire-and-forget contract: returns <see cref="Task.CompletedTask"/>
    ///     synchronously. The caller (<see cref="FingerprintAbsorptionService"/>)
    ///     computes centroid + weights before this call and chains its sequential
    ///     per-fingerprint loop on those local values -- no read-after-write
    ///     on the store is needed. The backstop tick absorbs any writes the
    ///     drainer has not yet committed if the process restarts.
    /// </summary>
    public Task AbsorbObservationAsync(
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
        var now = DateTime.UtcNow;

        // Write-behind convention (mirrors UpdateInducedNameAsync): update the hot
        // read cache synchronously so a read-after-write via GetFingerprintAsync sees
        // the absorbed centroid immediately; the SQLite fold lands off-path via the
        // drainer. Absorb was the one write-behind path that invalidated-after-persist
        // instead of updating the dict, which left the centroid stale until the async
        // drain ran (the read-after-write race that reds CentroidLearningLoopTests).
        if (_fingerprintById.TryGetValue(fingerprintId, out var fp))
        {
            var updated = fp with
            {
                Centroid = newCentroid,
                CentroidMaturity = newMaturity,
                Weights = newWeights,
            };
            if (newInferredClientType is not null)
            {
                updated = updated with
                {
                    InferredClientType = newInferredClientType,
                    InferredTypeConfidence = newInferredTypeConfidence,
                    InferredTypeChangedAt = inferredTypeChanged ? now : fp.InferredTypeChangedAt,
                };
            }
            _fingerprintById[fingerprintId] = updated;
        }

        EnsureNameDrainerStarted();
        _nameWriteChannel.Writer.TryWrite(new AbsorbWrite(
            fingerprintId, observationId, newCentroid, newMaturity, newWeights,
            newInferredClientType, newInferredTypeConfidence, inferredTypeChanged,
            now));
        return Task.CompletedTask;
    }

    /// <summary>
    ///     The actual absorption SQLite transaction, executed on the single-writer
    ///     drainer task. Mirrors the old <c>AbsorbObservationAsync</c> body but
    ///     uses <see cref="CancellationToken.None"/> because the drainer is not
    ///     tied to any individual caller's cancellation token.
    /// </summary>
    private async Task PersistAbsorbWriteAsync(AbsorbWrite write)
    {
        await EnsureInitialisedAsync(CancellationToken.None).ConfigureAwait(false);
        await using var conn = await OpenConnectionWithVecAsync(CancellationToken.None).ConfigureAwait(false);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(CancellationToken.None).ConfigureAwait(false);

        await using (var fp = conn.CreateCommand())
        {
            fp.Transaction = tx;
            if (write.NewInferredClientType is not null)
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
                fp.Parameters.AddWithValue("@itype", write.NewInferredClientType);
                fp.Parameters.AddWithValue("@iconf", write.NewInferredTypeConfidence);
                fp.Parameters.AddWithValue("@ichanged", write.InferredTypeChanged);
                fp.Parameters.AddWithValue("@now", write.At.ToString("O"));
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
            fp.Parameters.AddWithValue("@centroid", FloatsToBlob(write.NewCentroid));
            fp.Parameters.AddWithValue("@maturity", write.NewMaturity);
            fp.Parameters.AddWithValue("@weights", FloatsToBlob(write.NewWeights));
            fp.Parameters.AddWithValue("@id", write.FingerprintId);
            await fp.ExecuteNonQueryAsync().ConfigureAwait(false);
        }

        await using (var obs = conn.CreateCommand())
        {
            obs.Transaction = tx;
            obs.CommandText = "UPDATE fingerprint_observations SET absorbed_at = @ts WHERE id = @id";
            obs.Parameters.AddWithValue("@ts", write.At.ToString("O"));
            obs.Parameters.AddWithValue("@id", write.ObservationId);
            await obs.ExecuteNonQueryAsync().ConfigureAwait(false);
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
                del.Parameters.AddWithValue("@id", write.FingerprintId);
                await del.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            await using (var ins = conn.CreateCommand())
            {
                ins.Transaction = tx;
                ins.CommandText = "INSERT INTO fingerprints_vec(fingerprint_id, centroid) VALUES (@id, @vec)";
                ins.Parameters.AddWithValue("@id", write.FingerprintId);
                ins.Parameters.AddWithValue("@vec", FloatsToBlob(write.NewCentroid));
                await ins.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
            // Drop the absorbed observation from the active vec0 index — it's been
            // folded into the centroid; keeping it would double-count in KNN searches.
            await using (var obsDel = conn.CreateCommand())
            {
                obsDel.Transaction = tx;
                obsDel.CommandText = "DELETE FROM observations_vec WHERE observation_id = @id";
                obsDel.Parameters.AddWithValue("@id", write.ObservationId);
                await obsDel.ExecuteNonQueryAsync().ConfigureAwait(false);
            }
        }

        await tx.CommitAsync().ConfigureAwait(false);
        // No cache invalidation here: AbsorbObservationAsync already updated the hot
        // dict synchronously, so it is authoritative for the read path (same contract
        // as the name-write drainer, which also leaves the dict untouched). Invalidating
        // would drop the fresh entry and force a re-read that could race a later in-flight
        // absorb.
        Interlocked.Increment(ref AbsorbWriteCount);
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
        int maxFingerprints,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var results = new List<AbsorbableObservation>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        // Cap the working set to the most-recently-seen fingerprints that still have unabsorbed
        // observations (last_seen DESC = the DB corollary of the in-memory LFU). The fingerprint
        // that triggered a fast-path fold sorts to the top, so it is always in-set; colder ones
        // age in over successive backstop ticks. A non-positive cap means unbounded (legacy).
        if (maxFingerprints > 0)
        {
            cmd.CommandText = """
                SELECT o.id, o.fingerprint_id, o.vector, o.observed_at,
                       f.centroid, f.centroid_maturity, f.weights, f.observation_count, f.last_seen,
                       f.inferred_client_type, o.ua_family, o.domain, o.host
                  FROM fingerprint_observations o
                  JOIN fingerprints f ON f.fingerprint_id = o.fingerprint_id
                 WHERE o.absorbed_at IS NULL
                   AND o.fingerprint_id IN (
                       SELECT o2.fingerprint_id
                         FROM fingerprint_observations o2
                         JOIN fingerprints f2 ON f2.fingerprint_id = o2.fingerprint_id
                        WHERE o2.absorbed_at IS NULL
                        GROUP BY o2.fingerprint_id
                        ORDER BY f2.last_seen DESC
                        LIMIT @maxFingerprints
                   )
                """;
            cmd.Parameters.AddWithValue("@maxFingerprints", maxFingerprints);
        }
        else
        {
            cmd.CommandText = """
                SELECT o.id, o.fingerprint_id, o.vector, o.observed_at,
                       f.centroid, f.centroid_maturity, f.weights, f.observation_count, f.last_seen,
                       f.inferred_client_type, o.ua_family, o.domain, o.host
                  FROM fingerprint_observations o
                  JOIN fingerprints f ON f.fingerprint_id = o.fingerprint_id
                 WHERE o.absorbed_at IS NULL
                """;
        }
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
                UaFamily = reader.IsDBNull(10) ? null : reader.GetString(10),
                Domain = reader.IsDBNull(11) ? null : reader.GetString(11),
                Host = reader.IsDBNull(12) ? null : reader.GetString(12),
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
                   induced_name, induced_name_updated_at,
                   llm_name, llm_evaluated_at, llm_description,
                   given_name, given_name_updated_at, given_name_operator_id,
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
                   induced_name, induced_name_updated_at,
                   llm_name, llm_evaluated_at, llm_description,
                   given_name, given_name_updated_at, given_name_operator_id,
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
    ///     SINGLE SOURCE OF TRUTH for the stored risk band. The band is a pure
    ///     function of the stored probability and the fingerprint's inferred-type
    ///     confidence (<see cref="Risk.SignatureRiskVerdictComposer.BucketRisk"/>).
    ///     No caller-supplied band is EVER persisted verbatim — that is what let a
    ///     cold-start request (RecordVerdictAsync), an AI opinion whose free-text
    ///     label disagreed with its own probability (UpdateCachedVerdictAsync), or a
    ///     matcher's hardcoded band (the insert path) stamp a band that contradicted
    ///     the probability the dashboard shows (e.g. "prob 0.545 / band VeryHigh").
    ///     Deriving here guarantees the stored (probability, band) pair is always
    ///     internally consistent; the dashboard reads both as-is without recomputing.
    /// </summary>
    private static string DeriveConsistentBand(double probability, double confidence) =>
        Risk.SignatureRiskVerdictComposer
            .BucketRisk(probability, confidence)
            .ToString();

    /// <summary>
    ///     Writes a new cached verdict to the fingerprint row. Used by the manual AI opinion
    ///     path so an operator-triggered classifier verdict updates the row live without
    ///     waiting for the next drift tick. Touches <c>cached_bot_probability</c>,
    ///     <c>cached_risk_band</c>, and <c>cached_score_updated_at</c> in one transaction.
    ///     The caller's free-text band label is IGNORED for the stored band — it is
    ///     derived from the probability being written (see <see cref="DeriveConsistentBand"/>)
    ///     so the AI path cannot introduce a prob/band disagreement.
    /// </summary>
    public async Task UpdateCachedVerdictAsync(
        string fingerprintId, double botProbability, string? riskBand, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;

        // LFU-FIRST: the in-memory fingerprint dict is the live source of truth. Load it
        // (cold-load if not resident), update it IN PLACE, then enqueue the durability
        // write on the same drainer the hot path uses. NEVER write the DB and then evict
        // -- that leaves every OTHER live reader (the dashboard signature-aggregate LFU)
        // serving the pre-write score, producing the top-bots/detail divergence. Mirrors
        // RecordVerdictWriteBehind; the only difference is the operator/AI verdict is
        // authoritative so the probability is SET directly, not EWMA-blended.
        if (!_fingerprintById.TryGetValue(fingerprintId, out var existing))
        {
            existing = await GetFingerprintAsync(fingerprintId, ct);
            if (existing is null) return;
        }

        // Band is DERIVED from the probability (never the caller's free-text label) so a
        // prob/band disagreement can't be introduced.
        var consistentBand = DeriveConsistentBand(botProbability, existing.InferredTypeConfidence);
        var now = DateTime.UtcNow;

        _fingerprintById[fingerprintId] = existing with
        {
            CachedBotProbability = botProbability,
            CachedRiskBand       = consistentBand,
            CachedScoreUpdatedAt = now
        };

        EnsureNameDrainerStarted();
        _nameWriteChannel.Writer.TryWrite(new VerdictWrite(fingerprintId, botProbability, consistentBand, now));
    }

    /// <summary>
    ///     Hot-path verdict record. EWMA-blends the per-request probability into the in-memory
    ///     fingerprint (dict-first, the source of truth on the hot read path) and enqueues the
    ///     durability UPDATE on the shared name drainer. Unlike <see cref="RecordVerdictAsync"/>
    ///     it opens NO SQLite connection on the caller thread, so it is safe to call per request
    ///     from the detection path: the identity headline score converges to live detection
    ///     instead of only updating at a session-persistence boundary (a burst-bot that never
    ///     forms a 30-min session previously kept its allocation-time 0.0 and read as Human).
    ///     First-ever write is a direct assignment so a brand-new fingerprint's first detection
    ///     lands its real probability, not an attenuated blend against the default 0.0. The risk
    ///     band is derived from the blended score (never a caller-supplied band) so it can never
    ///     disagree with the probability the dashboard header re-buckets from CachedBotProbability.
    ///     Dict-only: if the fingerprint is not resident it is skipped (the matcher already
    ///     resident-loaded it earlier this request), never a cold-load on the hot path.
    /// </summary>
    public void RecordVerdictWriteBehind(string fingerprintId, double botProbability)
    {
        if (string.IsNullOrEmpty(fingerprintId)) return;
        if (!_fingerprintById.TryGetValue(fingerprintId, out var existing)) return;

        var alpha = Math.Clamp(_engineOptions.VerdictEwmaAlpha, 0.0, 1.0);
        var blended = existing.CachedScoreUpdatedAt is null
            ? botProbability
            : existing.CachedBotProbability * (1.0 - alpha) + botProbability * alpha;
        var consistentBand = DeriveConsistentBand(blended, existing.InferredTypeConfidence);
        var now = DateTime.UtcNow;

        // Dict-first: source of truth on the hot read path; the drainer write is durability only.
        _fingerprintById[fingerprintId] = existing with
        {
            CachedBotProbability = blended,
            CachedRiskBand       = consistentBand,
            CachedScoreUpdatedAt = now
        };

        EnsureNameDrainerStarted();
        _nameWriteChannel.Writer.TryWrite(new VerdictWrite(fingerprintId, blended, consistentBand, now));
    }

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

        // CONSISTENCY: the stored band MUST agree with the stored probability.
        // The probability is EWMA-blended (smoothed across requests), but the
        // incoming per-request `riskBand` reflects a SINGLE request — a
        // cold-start request that scored VeryHigh would stamp the band VeryHigh
        // while the blended probability settles to e.g. 0.26, leaving the row
        // "prob 0.26 / band VeryHigh". Derive the band from the blended
        // probability here (the gateway is the single compute site; the
        // dashboard only reads). The per-request band still appears per-row in
        // the detections history; the IDENTITY band is a function of the
        // identity probability.
        var consistentBand = DeriveConsistentBand(blended, existing.InferredTypeConfidence);

        var now = DateTime.UtcNow;
        var updated = existing with
        {
            CachedBotProbability = blended,
            CachedRiskBand       = consistentBand,
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

    // ── Durable bounding (identity data guardians, Part B) ───────────────────

    /// <inheritdoc/>
    public async Task<int> GetFingerprintCountAsync(CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM fingerprints";
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<FingerprintPriorityInfo>> GetAllFingerprintPriorityInfoAsync(
        int limit, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        var results = new List<FingerprintPriorityInfo>();
        if (limit <= 0) return results;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Oldest-first: stale fingerprints are the likeliest eviction candidates.
        // The guardian re-ranks this coarse pre-filter by DecisionNecessity.
        cmd.CommandText = """
            SELECT fingerprint_id, cached_bot_probability, cached_risk_band,
                   last_seen, claim_status
              FROM fingerprints
             ORDER BY last_seen ASC
             LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var botProb = reader.IsDBNull(1) ? 0.0 : reader.GetDouble(1);
            var band = reader.IsDBNull(2) ? null : reader.GetString(2);
            var lastSeen = ParseIso(reader.GetString(3));
            var claim = reader.IsDBNull(4) ? "unverified" : reader.GetString(4);
            results.Add(new FingerprintPriorityInfo(
                id, botProb, band, lastSeen, Protected: claim == "verified"));
        }
        return results;
    }

    /// <inheritdoc/>
    public async Task<int> DeleteFingerprintsAsync(
        IReadOnlyList<string> fingerprintIds, CancellationToken ct = default)
    {
        if (fingerprintIds is null || fingerprintIds.Count == 0) return 0;

        await EnsureInitialisedAsync(ct);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        var paramNames = new string[fingerprintIds.Count];
        for (var i = 0; i < fingerprintIds.Count; i++) paramNames[i] = "@fp" + i;
        var inClause = string.Join(",", paramNames);

        void Bind(SqliteCommand cmd)
        {
            cmd.Transaction = tx;
            for (var i = 0; i < fingerprintIds.Count; i++)
                cmd.Parameters.AddWithValue(paramNames[i], fingerprintIds[i]);
        }

        // observations_vec is keyed on observation_id, so delete its rows via the
        // owning observation ids BEFORE the observation rows go. Only present when
        // the vec extension loaded.
        if (_vecAvailable)
        {
            await using var vecObs = conn.CreateCommand();
            Bind(vecObs);
            vecObs.CommandText =
                $"DELETE FROM observations_vec WHERE observation_id IN " +
                $"(SELECT id FROM fingerprint_observations WHERE fingerprint_id IN ({inClause}))";
            try { await vecObs.ExecuteNonQueryAsync(ct); }
            catch (SqliteException) { /* vec table may be absent */ }

            await using var vecFp = conn.CreateCommand();
            Bind(vecFp);
            vecFp.CommandText = $"DELETE FROM fingerprints_vec WHERE fingerprint_id IN ({inClause})";
            try { await vecFp.ExecuteNonQueryAsync(ct); }
            catch (SqliteException) { /* vec table may be absent */ }
        }

        // Child tables before the parent so REFERENCES fingerprints(...) FKs hold.
        // fingerprint_corrections keys the fingerprint via pass2_fingerprint.
        var childTables = new (string Table, string Column)[]
        {
            ("fingerprint_observations", "fingerprint_id"),
            ("fingerprint_keys",         "fingerprint_id"),
            ("fingerprint_corrections",  "pass2_fingerprint"),
            ("fingerprint_name_history", "fingerprint_id"),
            ("fingerprint_root_history", "fingerprint_id")
        };
        foreach (var (table, column) in childTables)
        {
            await using var cmd = conn.CreateCommand();
            Bind(cmd);
            cmd.CommandText = $"DELETE FROM {table} WHERE {column} IN ({inClause})";
            await cmd.ExecuteNonQueryAsync(ct);
        }

        // Browser-mode tables live in this same fingerprints.db (written by
        // SqliteFingerprintBrowserModeStore). They declare ON DELETE CASCADE but
        // PRAGMA foreign_keys is off on this connection, so the cascade never fires:
        // evicting a fingerprint must delete these explicitly or fingerprint_mode_observations
        // (append-per-request, same shape as fingerprint_observations) keeps growing.
        // Wrapped in try/catch because a fingerprints.db that predates the browser-mode
        // store may not have these tables. Child (mode_observations) before parent (modes).
        foreach (var table in new[] { "fingerprint_mode_observations", "fingerprint_modes" })
        {
            await using var cmd = conn.CreateCommand();
            Bind(cmd);
            cmd.CommandText = $"DELETE FROM {table} WHERE fingerprint_id IN ({inClause})";
            try { await cmd.ExecuteNonQueryAsync(ct); }
            catch (SqliteException) { /* browser-mode tables may be absent */ }
        }

        int deleted;
        await using (var cmd = conn.CreateCommand())
        {
            Bind(cmd);
            cmd.CommandText = $"DELETE FROM fingerprints WHERE fingerprint_id IN ({inClause})";
            deleted = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);

        foreach (var id in fingerprintIds) InvalidateFingerprintCache(id);
        return deleted;
    }

    /// <inheritdoc/>
    public async Task<int> PruneAbsorbedObservationsAsync(
        int keepPerFingerprint, CancellationToken ct = default)
    {
        if (keepPerFingerprint < 0) keepPerFingerprint = 0;

        await EnsureInitialisedAsync(ct);
        await using var conn = await OpenConnectionWithVecAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);

        // Victim set: absorbed rows ranked beyond the newest-K per fingerprint by id.
        // Unabsorbed rows (absorbed_at IS NULL) are never in the ranking, so they
        // always survive -- this preserves both drift readers (ORDER BY id DESC and
        // observed_at DESC over absorbed rows), provided K >= their per-archetype cap.
        const string victimCte = """
            WITH ranked AS (
                SELECT id,
                       ROW_NUMBER() OVER (
                           PARTITION BY fingerprint_id
                           ORDER BY id DESC
                       ) AS rn
                  FROM fingerprint_observations
                 WHERE absorbed_at IS NOT NULL
            )
            SELECT id FROM ranked WHERE rn > @keep
            """;

        // Drop the vec mirror rows for the victims first (keyed on observation_id).
        if (_vecAvailable)
        {
            await using var vec = conn.CreateCommand();
            vec.Transaction = tx;
            vec.CommandText =
                $"DELETE FROM observations_vec WHERE observation_id IN ({victimCte})";
            vec.Parameters.AddWithValue("@keep", keepPerFingerprint);
            try { await vec.ExecuteNonQueryAsync(ct); }
            catch (SqliteException) { /* vec table may be absent */ }
        }

        int pruned;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText =
                $"DELETE FROM fingerprint_observations WHERE id IN ({victimCte})";
            cmd.Parameters.AddWithValue("@keep", keepPerFingerprint);
            pruned = await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
        return pruned;
    }

    private static DateTime ParseIso(string value) =>
        DateTime.TryParse(
            value, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal |
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var parsed)
            ? parsed
            : DateTime.UtcNow;

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
    ///     populate both dicts with what we loaded, and return signature -> resolved
    ///     name (<c>given ?? llm ?? induced</c>) via <see cref="FingerprintNameResolver"/>.
    ///     On a hot cache this never touches SQL.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, string?>> GetResolvedNamesBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures, CancellationToken ct)
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
                result[sig] = FingerprintNameResolver.Resolve(fp);
                continue;
            }

            (missingSigs ??= new List<string>(primarySignatures.Count)).Add(sig);
        }

        if (missingSigs is null || missingSigs.Count == 0) return result;

        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Cold-miss batch: resolve sig -> fingerprint_id + all three slot
        // columns in one roundtrip so the resolver can run downstream without
        // a second SELECT per row.
        var sb = new System.Text.StringBuilder();
        sb.Append("""
            SELECT k.primary_signature, f.fingerprint_id,
                   f.induced_name, f.llm_name, f.given_name
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
            var induced = reader.IsDBNull(2) ? null : reader.GetString(2);
            var llm = reader.IsDBNull(3) ? null : reader.GetString(3);
            var given = reader.IsDBNull(4) ? null : reader.GetString(4);
            _fingerprintIdByPrimarySig[sig] = fpId;
            // Resolve inline rather than constructing a transient Fingerprint
            // record — saves the heap alloc + Centroid clone on a hot read path.
            var resolved = given ?? llm ?? induced;
            result[sig] = string.IsNullOrEmpty(resolved) ? null : resolved;
        }

        foreach (var sig in missingSigs)
            if (!result.ContainsKey(sig))
                result[sig] = null;

        return result;
    }

    /// <summary>
    ///     Bulk transparent-LFU read for the dashboard's score/verdict scalars. Mirror
    ///     of <see cref="GetResolvedNamesBySignaturesAsync"/> for the verdict fields:
    ///     for each signature, resolve <c>sig -&gt; fingerprint_id -&gt; Fingerprint</c>
    ///     through the same two in-memory LFU dicts (<c>_fingerprintIdByPrimarySig</c> +
    ///     <c>_fingerprintById</c>) the name read uses, and project the resident
    ///     fingerprint to a <see cref="ResolvedVerdict"/>. Never touches the DB; reads
    ///     the in-memory LFU map only. Signatures whose fingerprint is not resident are
    ///     omitted (caller falls back to 0/null defaults, same as the name read returns
    ///     null until resolved).
    /// </summary>
    public Task<IReadOnlyDictionary<string, ResolvedVerdict>> GetResolvedVerdictsBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures, CancellationToken ct)
    {
        var result = new Dictionary<string, ResolvedVerdict>(StringComparer.Ordinal);
        if (primarySignatures.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, ResolvedVerdict>>(result);

        foreach (var sig in primarySignatures)
        {
            if (string.IsNullOrEmpty(sig)) continue;
            if (result.ContainsKey(sig)) continue;

            if (_fingerprintIdByPrimarySig.TryGetValue(sig, out var fpId)
                && _fingerprintById.TryGetValue(fpId, out var fp))
            {
                result[sig] = ProjectVerdict(fp);
            }
            // Cache miss: leave the signature out. Unlike the name read this does NOT
            // fall back to a SQL roundtrip -- the verdict scalars are the freshest on
            // the resident dict (write-behind LFU façade owns them), and a cold-miss
            // signature has no row the dashboard is actively displaying yet. The caller
            // treats a missing entry as "not resolved yet" and renders 0/null defaults.
        }

        return Task.FromResult<IReadOnlyDictionary<string, ResolvedVerdict>>(result);
    }

    /// <summary>
    ///     Project a resident <see cref="Fingerprint"/> to the dashboard's
    ///     <see cref="ResolvedVerdict"/>. BotType is the fingerprint's REAL inferred
    ///     type; a null/empty or literal "unknown"/"Unknown" inferred type projects to
    ///     null so the view falls through rather than emitting a placeholder. IsBot is
    ///     derived from the cached probability with the 0.5 cut the dashboard projection
    ///     paths already use (the store carries no reachable BotThreshold). IsVerifiedBot
    ///     reads the fingerprint's persistent claim state.
    /// </summary>
    private static ResolvedVerdict ProjectVerdict(Fingerprint fp)
    {
        var botType = string.IsNullOrEmpty(fp.InferredClientType)
                      || fp.InferredClientType.Equals("unknown", StringComparison.OrdinalIgnoreCase)
            ? null
            : fp.InferredClientType;

        return new ResolvedVerdict(
            BotProbability: fp.CachedBotProbability,
            RiskBand: fp.CachedRiskBand,
            BotType: botType,
            Confidence: fp.InferredTypeConfidence,
            ThreatScore: null,
            ThreatBand: null,
            IsBot: fp.CachedBotProbability >= 0.5,
            IsVerifiedBot: string.Equals(fp.ClaimStatus, "verified", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    ///     Atom-walk enumeration for the LLM picker. Per spec §3 + NS7 / §4a constraint:
    ///     never opens a DB connection — walks the in-memory <c>_fingerprintById</c>
    ///     dict. Returns hot fingerprints whose induced has drifted since the last LLM
    ///     eval (or never been evaluated). Sorted by <c>InducedNameUpdatedAt</c>
    ///     descending so the most recently shifted shapes get re-named first.
    /// </summary>
    public IReadOnlyList<Fingerprint> EnumerateLlmRepickCandidates(int maxCount)
    {
        if (maxCount <= 0) return Array.Empty<Fingerprint>();

        var candidates = new List<Fingerprint>();
        foreach (var fp in _fingerprintById.Values)
        {
            // No induced name means the matcher never projected this shape (or
            // banned-shape gate cleared it). Nothing for the LLM to react to —
            // skip rather than burn a token budget on an empty prior.
            if (string.IsNullOrEmpty(fp.InducedName)) continue;

            if (fp.LlmEvaluatedAt is null)
            {
                candidates.Add(fp);
                continue;
            }

            if (fp.InducedNameUpdatedAt is { } iAt && iAt > fp.LlmEvaluatedAt)
                candidates.Add(fp);
        }

        // Surrogate ordering: no hot-score on Fingerprint today, so "recently
        // updated" is the proxy for "shape that just moved". Real hot-score
        // landing here is a future-work item per NS7 plan note.
        candidates.Sort((a, b) =>
            Nullable.Compare(b.InducedNameUpdatedAt, a.InducedNameUpdatedAt));

        if (candidates.Count > maxCount)
            candidates.RemoveRange(maxCount, candidates.Count - maxCount);

        return candidates;
    }

    /// <summary>
    ///     Header search hit-list. Walks the in-memory LFU map only -- per
    ///     <c>feedback_write_behind_lfu_facade</c> the dict is truth, and the
    ///     header search is a hot UI path that must not pay a DB roundtrip on
    ///     every keystroke. Returns at most <paramref name="maxResults"/> hits
    ///     sorted by <c>LastSeen</c> descending; empty term short-circuits to
    ///     empty (we never return the whole map). The reverse-map walk to
    ///     recover the primary signature is O(N) over the binding cache; with
    ///     LFU capped at 10k that's fine for an interactive search but warrants
    ///     a forward map if the LFU is ever resized up.
    /// </summary>
    public Task<IReadOnlyList<FingerprintSearchHit>> SearchByResolvedNameAsync(
        string term, int maxResults, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(term) || maxResults <= 0)
            return Task.FromResult<IReadOnlyList<FingerprintSearchHit>>(Array.Empty<FingerprintSearchHit>());

        var hits = new List<FingerprintSearchHit>();
        foreach (var fp in _fingerprintById.Values)
        {
            var name = FingerprintNameResolver.Resolve(fp);
            if (string.IsNullOrEmpty(name)) continue;
            if (!name.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;

            // Reverse-map walk: the binding cache is keyed sig -> fpId so we
            // scan it to recover any primary signature that points at this
            // fingerprint. First-match wins; the dashboard only needs one to
            // build the navigation link.
            string? sig = null;
            foreach (var kv in _fingerprintIdByPrimarySig)
            {
                if (string.Equals(kv.Value, fp.FingerprintId, StringComparison.Ordinal))
                {
                    sig = kv.Key;
                    break;
                }
            }

            hits.Add(new FingerprintSearchHit(
                FingerprintId: fp.FingerprintId,
                PrimarySignature: sig ?? string.Empty,
                ResolvedName: name,
                LastSeen: fp.LastSeen));
        }

        var sorted = hits
            .OrderByDescending(h => h.LastSeen)
            .Take(maxResults)
            .ToList();
        return Task.FromResult<IReadOnlyList<FingerprintSearchHit>>(sorted);
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
            SELECT old_name, new_name, source, changed_at, signal_snapshot_json
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
            var snap = reader.IsDBNull(4) ? null : reader.GetString(4);
            list.Add(new DisplayNameChange(old, @new, src, ts, snap));
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
                 descendant_count, last_refined_at, variance_multiplier)
                VALUES (@id, @name, @desc, @centroid, @mask, @kind, @count, @ts, @vmult)
                ON CONFLICT(archetype_id) DO UPDATE SET
                    centroid            = excluded.centroid,
                    descendant_count    = excluded.descendant_count,
                    last_refined_at     = excluded.last_refined_at,
                    variance_multiplier = excluded.variance_multiplier
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
                 descendant_count, last_refined_at, variance_multiplier)
                VALUES (@id, @name, @desc, @centroid, @mask, @kind, @count, @ts, @vmult)
                ON CONFLICT(archetype_id) DO NOTHING
            """;
        BindArchetypeParams(cmd, archetype);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    ///     <inheritdoc cref="IFingerprintStore.GetByCatalogueKindAsync"/>
    ///     SQLite read filtered on the <c>catalogue_kind</c> column. The
    ///     <c>centroid</c> BLOB rehydrates via the same
    ///     <c>BlobToFloats</c> helper that powers the rest of this store, so
    ///     dim conversion stays in one place.
    /// </summary>
    public async Task<IReadOnlyList<IdentityArchetypeRow>> GetByCatalogueKindAsync(
        string catalogueKind, CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT archetype_id, catalogue_kind, centroid, descendant_count
              FROM identity_archetypes
             WHERE catalogue_kind = @kind
            """;
        cmd.Parameters.AddWithValue("@kind", catalogueKind);
        var rows = new List<IdentityArchetypeRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var id = reader.GetString(0);
            var kind = reader.GetString(1);
            var blob = (byte[])reader.GetValue(2);
            var maturity = reader.GetInt64(3);
            rows.Add(new IdentityArchetypeRow(id, kind, BlobToFloats(blob), maturity));
        }
        return rows;
    }

    /// <summary>
    ///     <inheritdoc cref="IFingerprintStore.UpsertCentroidAsync"/>
    ///     SQLite upsert. On insert this populates the columns the existing
    ///     identity-archetype rows require (<c>name</c>, <c>archetype_kind</c>,
    ///     <c>dimension_mask</c>, <c>last_refined_at</c>) with neutral defaults
    ///     so the new row is valid for the existing read paths. On conflict
    ///     ONLY the centroid + maturity + last_refined_at are touched -- name /
    ///     kind / mask are owned by the YAML loader and stay stable across
    ///     drift updates.
    /// </summary>
    public async Task UpsertCentroidAsync(
        string archetypeId,
        string catalogueKind,
        float[] centroid,
        double maturity,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO identity_archetypes
                (archetype_id, name, description, centroid, dimension_mask, archetype_kind,
                 descendant_count, last_refined_at, variance_multiplier, catalogue_kind)
            VALUES (@id, @name, NULL, @centroid, @mask, @kind, @count, @ts, 1.0, @cat_kind)
            ON CONFLICT(archetype_id) DO UPDATE SET
                centroid          = excluded.centroid,
                descendant_count  = excluded.descendant_count,
                last_refined_at   = excluded.last_refined_at
            """;
        cmd.Parameters.AddWithValue("@id", archetypeId);
        cmd.Parameters.AddWithValue("@name", archetypeId);
        cmd.Parameters.AddWithValue("@centroid", FloatsToBlob(centroid));
        // dimension_mask is BLOB NOT NULL on legacy schemas; mode-centroid rows
        // don't carry a per-dim mask (mode classifiers project the full
        // centroid). Persist an empty BLOB rather than null so the column
        // constraint holds across SQLite + Postgres.
        cmd.Parameters.AddWithValue("@mask", Array.Empty<byte>());
        cmd.Parameters.AddWithValue("@kind", catalogueKind);
        cmd.Parameters.AddWithValue("@count", (int)Math.Round(maturity));
        cmd.Parameters.AddWithValue("@ts", DateTime.UtcNow.ToString("O"));
        cmd.Parameters.AddWithValue("@cat_kind", catalogueKind);
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
        cmd.Parameters.AddWithValue("@vmult", archetype.VarianceMultiplier);
    }

    /// <inheritdoc/>
    public async Task InsertDriftMetricsAsync(
        IReadOnlyList<ArchetypeDriftMetric> metrics,
        CancellationToken ct = default)
    {
        if (metrics.Count == 0) return;
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var tx = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO archetype_drift_metrics
                    (archetype_id, ua_family, matches_asserted_ua, descendant_count,
                     mean_l2_to_centroid, variance_l2_to_centroid, p90_l2_to_centroid,
                     calibrated_at)
                VALUES
                    (@id, @ua, @matches, @count, @mean, @var, @p90, @ts)
                ON CONFLICT(archetype_id, ua_family, calibrated_at) DO UPDATE SET
                    matches_asserted_ua    = excluded.matches_asserted_ua,
                    descendant_count       = excluded.descendant_count,
                    mean_l2_to_centroid    = excluded.mean_l2_to_centroid,
                    variance_l2_to_centroid = excluded.variance_l2_to_centroid,
                    p90_l2_to_centroid     = excluded.p90_l2_to_centroid
                """;
            // Reuse one prepared command for the whole batch -- typical batch is
            // O(archetypes × distinct-uas) so ~100-500 rows per calibration cycle.
            var pId    = cmd.Parameters.Add("@id", Microsoft.Data.Sqlite.SqliteType.Text);
            var pUa    = cmd.Parameters.Add("@ua", Microsoft.Data.Sqlite.SqliteType.Text);
            var pMatch = cmd.Parameters.Add("@matches", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pCount = cmd.Parameters.Add("@count", Microsoft.Data.Sqlite.SqliteType.Integer);
            var pMean  = cmd.Parameters.Add("@mean", Microsoft.Data.Sqlite.SqliteType.Real);
            var pVar   = cmd.Parameters.Add("@var", Microsoft.Data.Sqlite.SqliteType.Real);
            var pP90   = cmd.Parameters.Add("@p90", Microsoft.Data.Sqlite.SqliteType.Real);
            var pTs    = cmd.Parameters.Add("@ts", Microsoft.Data.Sqlite.SqliteType.Text);
            await cmd.PrepareAsync(ct);

            foreach (var m in metrics)
            {
                pId.Value    = m.ArchetypeId;
                pUa.Value    = m.UaFamily ?? string.Empty;
                pMatch.Value = m.MatchesAssertedUa ? 1 : 0;
                pCount.Value = m.DescendantCount;
                pMean.Value  = m.MeanL2ToCentroid;
                pVar.Value   = m.VarianceL2ToCentroid;
                pP90.Value   = m.P90L2ToCentroid;
                pTs.Value    = m.CalibratedAt.ToString("O");
                await cmd.ExecuteNonQueryAsync(ct);
            }
        }
        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<DriftObservationRow>> ListRecentObservationsForDriftAsync(
        int maxRowsPerArchetype,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Window-function shape: rank observations per fingerprint by observed_at
        // DESC, keep the most recent maxRowsPerArchetype per (archetype, ua) pair.
        // SQLite has supported ROW_NUMBER() OVER(...) since 3.25.0 (2018) and the
        // identity_core schema already uses CTEs elsewhere -- safe to assume.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            WITH ranked AS (
                SELECT
                    fp.inferred_client_type AS archetype_id,
                    COALESCE(o.ua_family, '') AS ua_family,
                    o.vector AS vector,
                    ROW_NUMBER() OVER (
                        PARTITION BY fp.inferred_client_type, COALESCE(o.ua_family, '')
                        ORDER BY o.observed_at DESC
                    ) AS rn
                FROM fingerprint_observations AS o
                JOIN fingerprints AS fp ON fp.fingerprint_id = o.fingerprint_id
                WHERE fp.inferred_client_type IS NOT NULL
                  AND fp.inferred_client_type != ''
            )
            SELECT archetype_id, ua_family, vector
              FROM ranked
             WHERE rn <= @cap
            """;
        cmd.Parameters.AddWithValue("@cap", maxRowsPerArchetype);

        var rows = new List<DriftObservationRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new DriftObservationRow(
                ArchetypeId: reader.GetString(0),
                UaFamily: reader.GetString(1),
                ObservationVector: BlobToFloats((byte[])reader.GetValue(2))));
        }
        return rows;
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<ArchetypeDriftMetric>> ListLatestDriftMetricsAsync(
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Latest-cycle filter: pull rows whose calibrated_at equals the max value
        // in the table. One SELECT with a correlated max(); cheap because
        // ix_adm_calibrated_at covers the max scan.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT archetype_id, ua_family, matches_asserted_ua, descendant_count,
                   mean_l2_to_centroid, variance_l2_to_centroid, p90_l2_to_centroid,
                   calibrated_at
              FROM archetype_drift_metrics
             WHERE calibrated_at = (SELECT MAX(calibrated_at) FROM archetype_drift_metrics)
             ORDER BY archetype_id, ua_family
            """;
        var rows = new List<ArchetypeDriftMetric>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(new ArchetypeDriftMetric
            {
                ArchetypeId         = reader.GetString(0),
                UaFamily            = reader.GetString(1),
                MatchesAssertedUa   = reader.GetInt32(2) != 0,
                DescendantCount     = reader.GetInt32(3),
                MeanL2ToCentroid    = reader.GetDouble(4),
                VarianceL2ToCentroid = reader.GetDouble(5),
                P90L2ToCentroid     = reader.GetDouble(6),
                CalibratedAt        = DateTime.Parse(reader.GetString(7), null,
                                       System.Globalization.DateTimeStyles.RoundtripKind),
            });
        }
        return rows;
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
        // Three-slot names (induced / llm / given) replace the old single display_name.
        // Each slot is independent; resolver picks given ?? llm ?? induced.
        InducedName = reader.IsDBNull(18) ? null : reader.GetString(18),
        InducedNameUpdatedAt = reader.IsDBNull(19) || string.IsNullOrEmpty(reader.GetString(19))
            ? null
            : DateTime.Parse(reader.GetString(19), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LlmName = reader.IsDBNull(20) ? null : reader.GetString(20),
        LlmEvaluatedAt = reader.IsDBNull(21)
            ? null
            : DateTime.Parse(reader.GetString(21), null, System.Globalization.DateTimeStyles.RoundtripKind),
        LlmDescription = reader.IsDBNull(22) ? null : reader.GetString(22),
        GivenName = reader.IsDBNull(23) ? null : reader.GetString(23),
        GivenNameUpdatedAt = reader.IsDBNull(24)
            ? null
            : DateTime.Parse(reader.GetString(24), null, System.Globalization.DateTimeStyles.RoundtripKind),
        GivenNameOperatorId = reader.IsDBNull(25) ? null : reader.GetString(25),
        RootCentroid = reader.IsDBNull(26) ? null : BlobToFloats((byte[])reader.GetValue(26)),
        RootCentroidAt = reader.IsDBNull(27)
            ? null
            : DateTime.Parse(reader.GetString(27), null, System.Globalization.DateTimeStyles.RoundtripKind),
        RootSource = reader.IsDBNull(28) ? null : reader.GetString(28),
        // Trust state (gap #4). Older rows pre-migration default to
        // 'unverified' / NULL / NULL / 0 via the ALTER TABLE column defaults.
        ClaimStatus = reader.IsDBNull(29) ? "unverified" : reader.GetString(29),
        VerificationMethod = reader.IsDBNull(30) ? null : reader.GetString(30),
        VerifiedAt = reader.IsDBNull(31)
            ? null
            : DateTime.Parse(reader.GetString(31), null, System.Globalization.DateTimeStyles.RoundtripKind),
        TrustObservations = reader.IsDBNull(32) ? 0 : reader.GetInt32(32),
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
                DisplayName: FingerprintNameResolver.Resolve(neighbour) ?? string.Empty,
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

