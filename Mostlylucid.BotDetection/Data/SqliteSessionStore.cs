using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Data;

/// <summary>
///     SQLite-backed session store. Zero external dependencies - just a file on disk.
///     Sessions are the unit of storage, not individual requests.
///     Vector search uses brute-force cosine similarity (fast enough for <100K sessions).
///     For larger deployments, the commercial PostgreSQL + pgvector implementation
///     provides native HNSW indexing for sub-millisecond vector queries.
/// </summary>
public sealed class SqliteSessionStore : ISessionStore, IAsyncDisposable
{
    private readonly string _connectionString;

    public string? PersistenceConnectionString => _connectionString;
    private readonly ILogger<SqliteSessionStore> _logger;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private Task? _initTask;
    private ISessionVectorSearch? _vectorSearch;

    public SqliteSessionStore(
        ILogger<SqliteSessionStore> logger,
        IOptions<BotDetectionOptions> options,
        ISessionVectorSearch? vectorSearch = null)
    {
        _vectorSearch = vectorSearch;
        _logger = logger;
        var basePath = Path.GetDirectoryName(
            options.Value.DatabasePath ?? Path.Combine(AppContext.BaseDirectory, "botdetection.db"))
            ?? AppContext.BaseDirectory;
        Directory.CreateDirectory(basePath);
        var dbPath = Path.Combine(basePath, "sessions.db");
        _connectionString = $"Data Source={dbPath};Cache=Shared";
    }

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-8000;

            CREATE TABLE IF NOT EXISTS sessions (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                signature TEXT NOT NULL,
                started_at TEXT NOT NULL,
                ended_at TEXT NOT NULL,
                request_count INTEGER NOT NULL,
                vector BLOB,
                maturity REAL NOT NULL,
                dominant_state TEXT NOT NULL,
                is_bot INTEGER NOT NULL,
                avg_bot_probability REAL NOT NULL,
                avg_confidence REAL NOT NULL,
                risk_band TEXT NOT NULL,
                action TEXT,
                bot_name TEXT,
                bot_type TEXT,
                country_code TEXT,
                top_reasons_json TEXT,
                transition_counts_json TEXT,
                paths_json TEXT,
                avg_processing_time_ms REAL,
                error_count INTEGER DEFAULT 0,
                timing_entropy REAL DEFAULT 0,
                narrative TEXT,
                header_hashes_json TEXT,
                user_agent_raw TEXT,
                frequency_fingerprint BLOB,
                drift_vector BLOB
            );

            CREATE INDEX IF NOT EXISTS idx_sessions_signature ON sessions(signature, ended_at DESC);
            CREATE INDEX IF NOT EXISTS idx_sessions_ended ON sessions(ended_at DESC);
            CREATE INDEX IF NOT EXISTS idx_sessions_is_bot ON sessions(is_bot, ended_at DESC);
            CREATE INDEX IF NOT EXISTS idx_sessions_country ON sessions(country_code);

            CREATE TABLE IF NOT EXISTS signatures (
                signature_id TEXT PRIMARY KEY,
                session_count INTEGER NOT NULL DEFAULT 0,
                total_request_count INTEGER NOT NULL DEFAULT 0,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                is_bot INTEGER NOT NULL DEFAULT 0,
                bot_probability REAL NOT NULL DEFAULT 0,
                confidence REAL NOT NULL DEFAULT 0,
                risk_band TEXT NOT NULL DEFAULT 'Low',
                bot_name TEXT,
                bot_type TEXT,
                action TEXT,
                country_code TEXT,
                root_vector BLOB,
                root_vector_maturity REAL DEFAULT 0,
                narrative TEXT,
                top_reasons_json TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_signatures_bot ON signatures(is_bot, session_count DESC);
            CREATE INDEX IF NOT EXISTS idx_signatures_last_seen ON signatures(last_seen DESC);

            CREATE TABLE IF NOT EXISTS buckets (
                bucket_time TEXT NOT NULL PRIMARY KEY,
                total_count INTEGER NOT NULL DEFAULT 0,
                bot_count INTEGER NOT NULL DEFAULT 0,
                human_count INTEGER NOT NULL DEFAULT 0,
                unique_signatures INTEGER NOT NULL DEFAULT 0,
                sessions_started INTEGER NOT NULL DEFAULT 0,
                avg_processing_time_ms REAL NOT NULL DEFAULT 0
            );

            CREATE INDEX IF NOT EXISTS idx_buckets_time ON buckets(bucket_time DESC);

            -- Entity Resolution: resolved actor identities
            -- An entity is our best guess at "this is one actor."
            -- Multiple PrimarySignatures can map to one entity (merge).
            -- One signature can fork into multiple entities (split).
            CREATE TABLE IF NOT EXISTS entities (
                entity_id TEXT PRIMARY KEY,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                confidence_level INTEGER NOT NULL DEFAULT 0,
                factor_count INTEGER NOT NULL DEFAULT 1,
                is_bot INTEGER NOT NULL DEFAULT 0,
                bot_probability REAL NOT NULL DEFAULT 0,
                reputation_score REAL NOT NULL DEFAULT 0,
                stable_anchors_json TEXT,
                rotation_cadence_seconds REAL,
                velocity_variance REAL,
                metadata_json TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_entities_updated ON entities(updated_at DESC);
            CREATE INDEX IF NOT EXISTS idx_entities_bot ON entities(is_bot, reputation_score DESC);

            -- Edges link PrimarySignatures to entities with audit trail
            CREATE TABLE IF NOT EXISTS entity_edges (
                edge_id TEXT PRIMARY KEY,
                entity_id TEXT NOT NULL,
                signature TEXT NOT NULL,
                edge_type TEXT NOT NULL,
                confidence REAL NOT NULL,
                created_at TEXT NOT NULL,
                reason TEXT,
                reverted_at TEXT,
                FOREIGN KEY (entity_id) REFERENCES entities(entity_id)
            );

            CREATE INDEX IF NOT EXISTS idx_edges_signature ON entity_edges(signature, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_edges_entity ON entity_edges(entity_id, created_at DESC);
            CREATE INDEX IF NOT EXISTS idx_edges_active ON entity_edges(signature) WHERE reverted_at IS NULL;

            CREATE TABLE IF NOT EXISTS requests (
                id              INTEGER PRIMARY KEY AUTOINCREMENT,
                signature       TEXT    NOT NULL,
                timestamp       TEXT    NOT NULL,
                path            TEXT    NOT NULL,
                markov_state    TEXT    NOT NULL,
                status_code     INTEGER NOT NULL,
                bot_probability REAL    NOT NULL,
                confidence      REAL    NOT NULL,
                risk_band       TEXT    NOT NULL,
                processing_ms   REAL    NOT NULL DEFAULT 0,
                session_id      INTEGER REFERENCES sessions(id) ON DELETE SET NULL
            );

            CREATE INDEX IF NOT EXISTS idx_requests_sig_time  ON requests(signature, timestamp ASC);
            CREATE INDEX IF NOT EXISTS idx_requests_unatomized ON requests(signature, timestamp ASC) WHERE session_id IS NULL;
            CREATE INDEX IF NOT EXISTS idx_requests_ts_desc   ON requests(timestamp DESC);
        """;
        await cmd.ExecuteNonQueryAsync(ct);

        // Schema migration: add columns introduced after initial release.
        // ALTER TABLE ADD COLUMN is idempotent in the sense that we catch duplicate-column errors.
        await MigrateAddColumnAsync(conn, "sessions", "frequency_fingerprint", "BLOB", ct);
        await MigrateAddColumnAsync(conn, "sessions", "drift_vector", "BLOB", ct);
        await MigrateAddColumnAsync(conn, "signatures", "frequency_centroid", "BLOB", ct);

        _logger.LogInformation("SQLite session store initialized at {ConnectionString}", _connectionString);
    }

    // === Schema migration helper ===

    private static async Task MigrateAddColumnAsync(
        SqliteConnection conn, string table, string column, string type, CancellationToken ct)
    {
        // Check if column already exists using PRAGMA table_info
        await using var checkCmd = conn.CreateCommand();
        checkCmd.CommandText = $"PRAGMA table_info({table})";
        await using var reader = await checkCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (string.Equals(reader.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                return; // Column already exists
        }

        // Add the column
        await using var alterCmd = conn.CreateCommand();
        alterCmd.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {type}";
        await alterCmd.ExecuteNonQueryAsync(ct);
    }

    // === Write path ===

    public async Task<long> AddSessionAsync(PersistedSession session, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO sessions (
                    signature, started_at, ended_at, request_count, vector, maturity,
                    dominant_state, is_bot, avg_bot_probability, avg_confidence, risk_band,
                    action, bot_name, bot_type, country_code, top_reasons_json,
                    transition_counts_json, paths_json, avg_processing_time_ms,
                    error_count, timing_entropy, narrative,
                    header_hashes_json, user_agent_raw,
                    frequency_fingerprint, drift_vector
                ) VALUES (
                    @sig, @started, @ended, @reqCount, @vector, @maturity,
                    @domState, @isBot, @avgProb, @avgConf, @risk,
                    @action, @botName, @botType, @country, @reasons,
                    @transitions, @paths, @avgTime,
                    @errors, @entropy, @narrative,
                    @headerHashes, @uaRaw,
                    @freqFp, @driftVec
                )
            """;
            cmd.Parameters.AddWithValue("@sig", session.Signature);
            cmd.Parameters.AddWithValue("@started", session.StartedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@ended", session.EndedAt.ToString("O"));
            cmd.Parameters.AddWithValue("@reqCount", session.RequestCount);
            cmd.Parameters.AddWithValue("@vector", session.Vector);
            cmd.Parameters.AddWithValue("@maturity", session.Maturity);
            cmd.Parameters.AddWithValue("@domState", session.DominantState);
            cmd.Parameters.AddWithValue("@isBot", session.IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@avgProb", session.AvgBotProbability);
            cmd.Parameters.AddWithValue("@avgConf", session.AvgConfidence);
            cmd.Parameters.AddWithValue("@risk", session.RiskBand);
            cmd.Parameters.AddWithValue("@action", (object?)session.Action ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@botName", (object?)session.BotName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@botType", (object?)session.BotType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", (object?)session.CountryCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reasons", (object?)session.TopReasonsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@transitions", (object?)session.TransitionCountsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@paths", (object?)session.PathsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@avgTime", session.AvgProcessingTimeMs);
            cmd.Parameters.AddWithValue("@errors", session.ErrorCount);
            cmd.Parameters.AddWithValue("@entropy", session.TimingEntropy);
            cmd.Parameters.AddWithValue("@narrative", (object?)session.Narrative ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@headerHashes", (object?)session.HeaderHashesJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@uaRaw", (object?)session.UserAgentRaw ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@freqFp", (object?)session.FrequencyFingerprintBlob ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@driftVec", (object?)session.DriftVectorBlob ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
            cmd.CommandText = "SELECT last_insert_rowid()";
            return (long)(await cmd.ExecuteScalarAsync(ct))!;
        }
        finally
        {
            _writeLock.Release();
            FeedSessionVectorToHnsw(session);
        }
    }

    private void FeedSessionVectorToHnsw(PersistedSession session)
    {
        if (_vectorSearch == null || session.Vector is not { Length: > 0 }) return;
        var vector = DeserializeVector(session.Vector);
        if (vector != null)
            _ = AddToVectorSearchAsync(vector, session.Signature, session.IsBot, session.AvgBotProbability,
                session.FrequencyFingerprintBlob, session.DriftVectorBlob);
    }

    public async Task UpsertSignatureAsync(PersistedSignature signature, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO signatures (
                    signature_id, session_count, total_request_count, first_seen, last_seen,
                    is_bot, bot_probability, confidence, risk_band,
                    bot_name, bot_type, action, country_code,
                    root_vector, root_vector_maturity, narrative, top_reasons_json
                ) VALUES (
                    @id, @sessions, @requests, @first, @last,
                    @isBot, @prob, @conf, @risk,
                    @botName, @botType, @action, @country,
                    @rootVec, @rootMat, @narrative, @reasons
                )
                ON CONFLICT(signature_id) DO UPDATE SET
                    session_count = session_count + @sessions,
                    total_request_count = total_request_count + @requests,
                    last_seen = @last,
                    -- Preserve the highest-confidence detection result seen so far.
                    -- Session finalization writes prob=0 (no per-session result yet), so
                    -- we must never let that overwrite a real classification from UpdateSignatureDetectionAsync.
                    is_bot = CASE WHEN @prob > bot_probability THEN @isBot ELSE is_bot END,
                    bot_probability = MAX(bot_probability, @prob),
                    confidence = MAX(confidence, @conf),
                    risk_band = CASE WHEN @prob > bot_probability THEN @risk ELSE risk_band END,
                    bot_name = COALESCE(CASE WHEN @prob > bot_probability THEN @botName ELSE NULL END, bot_name),
                    bot_type = COALESCE(CASE WHEN @prob > bot_probability THEN @botType ELSE NULL END, bot_type),
                    action = COALESCE(CASE WHEN @prob > bot_probability THEN @action ELSE NULL END, action),
                    country_code = COALESCE(@country, country_code),
                    root_vector = COALESCE(@rootVec, root_vector),
                    root_vector_maturity = CASE WHEN @rootMat > root_vector_maturity THEN @rootMat ELSE root_vector_maturity END,
                    narrative = COALESCE(@narrative, narrative),
                    top_reasons_json = COALESCE(@reasons, top_reasons_json)
            """;
            cmd.Parameters.AddWithValue("@id", signature.SignatureId);
            cmd.Parameters.AddWithValue("@sessions", signature.SessionCount);
            cmd.Parameters.AddWithValue("@requests", signature.TotalRequestCount);
            cmd.Parameters.AddWithValue("@first", signature.FirstSeen.ToString("O"));
            cmd.Parameters.AddWithValue("@last", signature.LastSeen.ToString("O"));
            cmd.Parameters.AddWithValue("@isBot", signature.IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", signature.BotProbability);
            cmd.Parameters.AddWithValue("@conf", signature.Confidence);
            cmd.Parameters.AddWithValue("@risk", signature.RiskBand);
            cmd.Parameters.AddWithValue("@botName", (object?)signature.BotName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@botType", (object?)signature.BotType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@action", (object?)signature.Action ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", (object?)signature.CountryCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rootVec", (object?)signature.RootVector ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@rootMat", signature.RootVectorMaturity);
            cmd.Parameters.AddWithValue("@narrative", (object?)signature.Narrative ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reasons", (object?)signature.TopReasonsJson ?? DBNull.Value);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task UpdateSignatureDetectionAsync(
        string signatureId,
        bool isBot,
        double botProbability,
        double confidence,
        string riskBand,
        string? botName,
        string? botType,
        string? action,
        DateTime requestTime,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            // Create row if new; update only classification fields on conflict.
            // session_count stays 0 until a session actually finalizes (via UpsertSignatureAsync).
            cmd.CommandText = """
                INSERT INTO signatures (
                    signature_id, session_count, total_request_count,
                    first_seen, last_seen,
                    is_bot, bot_probability, confidence, risk_band,
                    bot_name, bot_type, action
                ) VALUES (
                    @id, 0, 1,
                    @time, @time,
                    @isBot, @prob, @conf, @risk,
                    @botName, @botType, @action
                )
                ON CONFLICT(signature_id) DO UPDATE SET
                    total_request_count = total_request_count + 1,
                    last_seen           = @time,
                    is_bot              = @isBot,
                    bot_probability     = @prob,
                    confidence          = @conf,
                    risk_band           = @risk,
                    bot_name            = COALESCE(@botName, bot_name),
                    bot_type            = COALESCE(@botType, bot_type),
                    action              = COALESCE(@action, action)
                """;
            cmd.Parameters.AddWithValue("@id", signatureId);
            cmd.Parameters.AddWithValue("@time", requestTime.ToString("O"));
            cmd.Parameters.AddWithValue("@isBot", isBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", botProbability);
            cmd.Parameters.AddWithValue("@conf", confidence);
            cmd.Parameters.AddWithValue("@risk", riskBand);
            cmd.Parameters.AddWithValue("@botName", (object?)botName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@botType", (object?)botType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@action", (object?)action ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task AddRequestAsync(PersistedRequest request, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO requests
                    (signature, timestamp, path, markov_state, status_code,
                     bot_probability, confidence, risk_band, processing_ms)
                VALUES
                    (@sig, @ts, @path, @state, @sc,
                     @prob, @conf, @risk, @ms)
                """;
            cmd.Parameters.AddWithValue("@sig",   request.Signature);
            cmd.Parameters.AddWithValue("@ts",    request.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@path",  request.Path);
            cmd.Parameters.AddWithValue("@state", request.MarkovState);
            cmd.Parameters.AddWithValue("@sc",    request.StatusCode);
            cmd.Parameters.AddWithValue("@prob",  request.BotProbability);
            cmd.Parameters.AddWithValue("@conf",  request.Confidence);
            cmd.Parameters.AddWithValue("@risk",  request.RiskBand);
            cmd.Parameters.AddWithValue("@ms",    request.ProcessingMs);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task AddRequestBatchAsync(IReadOnlyList<PersistedRequest> requests, CancellationToken ct = default)
    {
        if (requests.Count == 0) return;
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = conn.BeginTransaction();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                INSERT INTO requests
                    (signature, timestamp, path, markov_state, status_code,
                     bot_probability, confidence, risk_band, processing_ms)
                VALUES
                    (@sig, @ts, @path, @state, @sc,
                     @prob, @conf, @risk, @ms)
                """;
            var pSig  = cmd.Parameters.Add("@sig",   SqliteType.Text);
            var pTs   = cmd.Parameters.Add("@ts",    SqliteType.Text);
            var pPath = cmd.Parameters.Add("@path",  SqliteType.Text);
            var pSt   = cmd.Parameters.Add("@state", SqliteType.Text);
            var pSc   = cmd.Parameters.Add("@sc",    SqliteType.Integer);
            var pProb = cmd.Parameters.Add("@prob",  SqliteType.Real);
            var pConf = cmd.Parameters.Add("@conf",  SqliteType.Real);
            var pRisk = cmd.Parameters.Add("@risk",  SqliteType.Text);
            var pMs   = cmd.Parameters.Add("@ms",    SqliteType.Real);

            foreach (var r in requests)
            {
                pSig.Value  = r.Signature;
                pTs.Value   = r.Timestamp.ToString("O");
                pPath.Value = r.Path;
                pSt.Value   = r.MarkovState;
                pSc.Value   = r.StatusCode;
                pProb.Value = r.BotProbability;
                pConf.Value = r.Confidence;
                pRisk.Value = r.RiskBand;
                pMs.Value   = r.ProcessingMs;
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<PersistedRequest>> GetUnatomizedRequestsAsync(
        int limit = 5000, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT id, signature, timestamp, path, markov_state,
                   status_code, bot_probability, confidence, risk_band, processing_ms
            FROM requests
            WHERE session_id IS NULL
            ORDER BY signature, timestamp ASC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@limit", limit);
        var results = new List<PersistedRequest>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new PersistedRequest
            {
                Id             = reader.GetInt64(0),
                Signature      = reader.GetString(1),
                Timestamp      = DateTime.Parse(reader.GetString(2), null,
                                     System.Globalization.DateTimeStyles.RoundtripKind),
                Path           = reader.GetString(3),
                MarkovState    = reader.GetString(4),
                StatusCode     = reader.GetInt32(5),
                BotProbability = reader.GetDouble(6),
                Confidence     = reader.GetDouble(7),
                RiskBand       = reader.GetString(8),
                ProcessingMs   = reader.GetDouble(9),
            });
        }
        return results;
    }

    public async Task LinkRequestsToSessionAsync(
        long sessionId, IReadOnlyList<long> requestIds, CancellationToken ct = default)
    {
        if (requestIds.Count == 0) return;
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = conn.BeginTransaction();
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            // SQLite has a 999-parameter limit; process in chunks of 500
            foreach (var chunk in requestIds.Chunk(500))
            {
                var paramNames = chunk.Select((_, i) => $"@id{i}").ToList();
                cmd.CommandText = $"UPDATE requests SET session_id = @sid WHERE id IN ({string.Join(',', paramNames)}) AND session_id IS NULL";
                cmd.Parameters.Clear();
                cmd.Parameters.AddWithValue("@sid", sessionId);
                for (var i = 0; i < chunk.Length; i++)
                    cmd.Parameters.AddWithValue(paramNames[i], chunk[i]);
                await cmd.ExecuteNonQueryAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    public async Task IncrementBucketAsync(DateTime bucketTime, bool isBot, double processingTimeMs, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var bucket = new DateTime(bucketTime.Year, bucketTime.Month, bucketTime.Day,
            bucketTime.Hour, bucketTime.Minute, 0, DateTimeKind.Utc);

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO buckets (bucket_time, total_count, bot_count, human_count, avg_processing_time_ms)
                VALUES (@time, 1, @bot, @human, @avgTime)
                ON CONFLICT(bucket_time) DO UPDATE SET
                    total_count = total_count + 1,
                    bot_count = bot_count + @bot,
                    human_count = human_count + @human,
                    avg_processing_time_ms = (avg_processing_time_ms * total_count + @avgTime) / (total_count + 1)
            """;
            cmd.Parameters.AddWithValue("@time", bucket.ToString("O"));
            cmd.Parameters.AddWithValue("@bot", isBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@human", isBot ? 0 : 1);
            cmd.Parameters.AddWithValue("@avgTime", processingTimeMs);

            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    // === Read path ===

    public async Task<List<PersistedSession>> GetSessionsAsync(string signature, int limit = 20, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        // Query by either waveform signature OR dashboard multi-factor signature ID
        cmd.CommandText = "SELECT * FROM sessions WHERE signature = @sig ORDER BY ended_at DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@sig", signature);
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadSessionsAsync(cmd, ct);
    }

    public async Task<List<PersistedSession>> GetRecentSessionsAsync(int limit = 50, bool? isBot = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (isBot.HasValue)
        {
            cmd.CommandText = "SELECT * FROM sessions WHERE is_bot = @isBot ORDER BY ended_at DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@isBot", isBot.Value ? 1 : 0);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM sessions ORDER BY ended_at DESC LIMIT @limit";
        }
        cmd.Parameters.AddWithValue("@limit", limit);
        return await ReadSessionsAsync(cmd, ct);
    }

    public async Task<PersistedSignature?> GetSignatureAsync(string signatureId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM signatures WHERE signature_id = @id";
        cmd.Parameters.AddWithValue("@id", signatureId);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSignature(reader) : null;
    }

    public async Task<List<PersistedSignature>> GetTopSignaturesAsync(int limit = 20, bool? isBot = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        if (isBot.HasValue)
        {
            cmd.CommandText = "SELECT * FROM signatures WHERE is_bot = @isBot ORDER BY session_count DESC LIMIT @limit";
            cmd.Parameters.AddWithValue("@isBot", isBot.Value ? 1 : 0);
        }
        else
        {
            cmd.CommandText = "SELECT * FROM signatures ORDER BY session_count DESC LIMIT @limit";
        }
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<PersistedSignature>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadSignature(reader));
        return results;
    }

    public async Task<DashboardSessionSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total_sessions,
                SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bot_sessions,
                SUM(CASE WHEN is_bot = 0 THEN 1 ELSE 0 END) as human_sessions,
                COUNT(DISTINCT signature) as unique_signatures,
                SUM(request_count) as total_requests,
                AVG(avg_processing_time_ms) as avg_processing_time,
                MAX(ended_at) as last_activity
            FROM sessions
        """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
            return new DashboardSessionSummary();

        return new DashboardSessionSummary
        {
            TotalSessions = reader.GetInt32(0),
            BotSessions = reader.GetInt32(1),
            HumanSessions = reader.GetInt32(2),
            UniqueSignatures = reader.GetInt32(3),
            TotalRequests = reader.IsDBNull(4) ? 0 : reader.GetInt32(4),
            AvgProcessingTimeMs = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
            LastActivityAt = reader.IsDBNull(6) ? null : DateTime.Parse(reader.GetString(6))
        };
    }

    public async Task<List<AggregatedBucket>> GetTimeSeriesAsync(DateTime start, DateTime end, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT bucket_time, total_count, bot_count, human_count,
                   unique_signatures, sessions_started, avg_processing_time_ms
            FROM buckets
            WHERE bucket_time >= @start AND bucket_time <= @end
            ORDER BY bucket_time ASC
        """;
        cmd.Parameters.AddWithValue("@start", start.ToString("O"));
        cmd.Parameters.AddWithValue("@end", end.ToString("O"));

        var results = new List<AggregatedBucket>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new AggregatedBucket
            {
                BucketTime = DateTime.Parse(reader.GetString(0)),
                TotalCount = reader.GetInt32(1),
                BotCount = reader.GetInt32(2),
                HumanCount = reader.GetInt32(3),
                UniqueSignatures = reader.GetInt32(4),
                SessionsStarted = reader.GetInt32(5),
                AvgProcessingTimeMs = reader.GetDouble(6)
            });
        }
        return results;
    }

    public async Task<List<CountrySessionStats>> GetCountryStatsAsync(int limit = 20, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT country_code,
                   COUNT(*) as total,
                   SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots,
                   SUM(CASE WHEN is_bot = 0 THEN 1 ELSE 0 END) as humans,
                   SUM(request_count) as requests
            FROM sessions
            WHERE country_code IS NOT NULL
            GROUP BY country_code
            ORDER BY total DESC
            LIMIT @limit
        """;
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<CountrySessionStats>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CountrySessionStats
            {
                CountryCode = reader.GetString(0),
                TotalSessions = reader.GetInt32(1),
                BotSessions = reader.GetInt32(2),
                HumanSessions = reader.GetInt32(3),
                TotalRequests = reader.GetInt32(4)
            });
        }
        return results;
    }

    public async Task<List<(PersistedSession Session, float Similarity)>> FindSimilarSessionsAsync(
        float[] queryVector, int topK = 10, float minSimilarity = 0.7f, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // Fast path: HNSW ANN index (O(log n), sub-millisecond)
        if (_vectorSearch != null)
        {
            var matches = await _vectorSearch.FindSimilarAsync(queryVector, topK, minSimilarity);
            var results = new List<(PersistedSession, float)>(matches.Count);
            foreach (var match in matches)
            {
                var sessions = await GetSessionsAsync(match.Signature, 1, ct);
                if (sessions.Count > 0)
                    results.Add((sessions[0], match.Similarity));
            }
            return results;
        }

        // Fallback: brute-force cosine scan (used when HNSW index is not registered,
        // e.g. during testing or if AddBotDetection() is called without AddSimilaritySearch())
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT * FROM sessions
            WHERE vector IS NOT NULL AND maturity >= 0.3
            ORDER BY ended_at DESC
            LIMIT 5000
        """;

        var candidates = new List<(PersistedSession session, float similarity)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var session = ReadSession(reader);
            var sessionVector = DeserializeVector(session.Vector);
            if (sessionVector == null) continue;

            var sim = Analysis.SessionVectorizer.CosineSimilarity(queryVector, sessionVector);
            if (sim >= minSimilarity)
                candidates.Add((session, sim));
        }

        return candidates
            .OrderByDescending(c => c.similarity)
            .Take(topK)
            .ToList();
    }

    // === Entity Resolution ===

    public async Task<List<string>> GetActiveEntityIdsAsync(DateTime cutoff, int limit = 100, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT e.entity_id FROM entities e
            INNER JOIN entity_edges ee ON e.entity_id = ee.entity_id AND ee.reverted_at IS NULL
            WHERE e.updated_at >= @cutoff
            ORDER BY e.updated_at DESC LIMIT @limit
        """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        cmd.Parameters.AddWithValue("@limit", limit);

        var entityIds = new List<string>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            entityIds.Add(reader.GetString(0));
        return entityIds;
    }

    /// <summary>Cosine similarity threshold for merging a new signature into an existing entity.</summary>
    private const float MergeSimilarityThreshold = 0.85f;

    /// <summary>Maximum age of entities to consider for merge candidates.</summary>
    private static readonly TimeSpan MergeCandidateWindow = TimeSpan.FromHours(6);

    public async Task<string> ResolveEntityAsync(string primarySignature, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // Check for existing active edge - exact match
        var existing = await GetEntityForSignatureAsync(primarySignature, ct);
        if (existing != null) return existing.EntityId;

        // Get the latest session vector for this new signature
        var sessions = await GetSessionsAsync(primarySignature, 1, ct);
        var newVector = sessions.Count > 0 && sessions[0].Vector is { Length: > 0 }
            ? DeserializeVector(sessions[0].Vector)
            : null;

        // Try to merge with a near-neighbor entity (rotation detection)
        if (newVector != null)
        {
            var mergeCandidate = await FindMergeCandidateAsync(newVector, primarySignature, ct);
            if (mergeCandidate != null)
            {
                await MergeSignatureAsync(mergeCandidate.Value.EntityId, primarySignature,
                    mergeCandidate.Value.Similarity,
                    $"cosine={mergeCandidate.Value.Similarity:F3}, behavioral_match=true",
                    ct);
                return mergeCandidate.Value.EntityId;
            }
        }

        // No merge candidate - create new entity
        var entityId = Guid.NewGuid().ToString("N")[..16];
        var now = DateTime.UtcNow.ToString("O");

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var tx = await conn.BeginTransactionAsync(ct);

            await using var entityCmd = conn.CreateCommand();
            entityCmd.CommandText = "INSERT INTO entities (entity_id, created_at, updated_at) VALUES (@id, @now, @now)";
            entityCmd.Parameters.AddWithValue("@id", entityId);
            entityCmd.Parameters.AddWithValue("@now", now);
            await entityCmd.ExecuteNonQueryAsync(ct);

            await using var edgeCmd = conn.CreateCommand();
            edgeCmd.CommandText = """
                INSERT INTO entity_edges (edge_id, entity_id, signature, edge_type, confidence, created_at, reason)
                VALUES (@eid, @entity, @sig, 'Initial', 1.0, @now, 'First observation')
            """;
            edgeCmd.Parameters.AddWithValue("@eid", Guid.NewGuid().ToString("N")[..16]);
            edgeCmd.Parameters.AddWithValue("@entity", entityId);
            edgeCmd.Parameters.AddWithValue("@sig", primarySignature);
            edgeCmd.Parameters.AddWithValue("@now", now);
            await edgeCmd.ExecuteNonQueryAsync(ct);

            await tx.CommitAsync(ct);
        }
        finally { _writeLock.Release(); }

        _logger.LogDebug("Created entity {EntityId} for signature {Signature}", entityId, primarySignature[..Math.Min(8, primarySignature.Length)]);
        return entityId;
    }

    /// <summary>
    ///     Find the best merge candidate among recent entities.
    ///     Compares the new signature's session vector against root vectors of existing entities.
    ///     Returns the entity ID and similarity if above threshold, null otherwise.
    /// </summary>
    private async Task<(string EntityId, float Similarity)?> FindMergeCandidateAsync(
        float[] newVector, string excludeSignature, CancellationToken ct)
    {
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Get recent entities with their most recent session vector (via their signatures)
        // Only consider entities updated within the merge window
        var cutoff = DateTime.UtcNow.Subtract(MergeCandidateWindow).ToString("O");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT DISTINCT e.entity_id, s.vector
            FROM entities e
            INNER JOIN entity_edges ee ON e.entity_id = ee.entity_id AND ee.reverted_at IS NULL
            INNER JOIN sessions s ON s.signature = ee.signature
            WHERE e.updated_at >= @cutoff
              AND ee.signature != @exclude
              AND s.vector IS NOT NULL
            ORDER BY s.ended_at DESC
            LIMIT 50
        """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff);
        cmd.Parameters.AddWithValue("@exclude", excludeSignature);

        string? bestEntityId = null;
        var bestSimilarity = 0f;

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var seenEntities = new HashSet<string>();
        while (await reader.ReadAsync(ct))
        {
            var entityId = reader.GetString(0);
            if (!seenEntities.Add(entityId)) continue; // One comparison per entity

            var vectorBytes = reader.IsDBNull(1) ? null : (byte[])reader[1];
            if (vectorBytes == null) continue;

            var candidateVector = DeserializeVector(vectorBytes);
            if (candidateVector == null) continue;

            var similarity = Analysis.SessionVectorizer.CosineSimilarity(newVector, candidateVector);
            if (similarity > bestSimilarity)
            {
                bestSimilarity = similarity;
                bestEntityId = entityId;
            }
        }

        if (bestEntityId != null && bestSimilarity >= MergeSimilarityThreshold)
        {
            _logger.LogInformation(
                "Merge candidate found: entity {Entity} similarity={Sim:F3} (threshold={Threshold})",
                bestEntityId, bestSimilarity, MergeSimilarityThreshold);
            return (bestEntityId, bestSimilarity);
        }

        return null;
    }

    public async Task<ResolvedEntity?> GetEntityForSignatureAsync(string primarySignature, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT e.* FROM entities e
            INNER JOIN entity_edges ee ON e.entity_id = ee.entity_id
            WHERE ee.signature = @sig AND ee.reverted_at IS NULL
            ORDER BY ee.created_at DESC LIMIT 1
        """;
        cmd.Parameters.AddWithValue("@sig", primarySignature);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntity(reader) : null;
    }

    public async Task<ResolvedEntity?> GetEntityAsync(string entityId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM entities WHERE entity_id = @id";
        cmd.Parameters.AddWithValue("@id", entityId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadEntity(reader) : null;
    }

    public async Task<List<EntityEdge>> GetEntityEdgesAsync(string entityId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM entity_edges WHERE entity_id = @id ORDER BY created_at DESC";
        cmd.Parameters.AddWithValue("@id", entityId);

        var edges = new List<EntityEdge>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            edges.Add(ReadEdge(reader));
        return edges;
    }

    public async Task MergeSignatureAsync(string entityId, string signature, double confidence, string reason, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO entity_edges (edge_id, entity_id, signature, edge_type, confidence, created_at, reason)
                VALUES (@eid, @entity, @sig, 'Merge', @conf, @now, @reason)
            """;
            cmd.Parameters.AddWithValue("@eid", Guid.NewGuid().ToString("N")[..16]);
            cmd.Parameters.AddWithValue("@entity", entityId);
            cmd.Parameters.AddWithValue("@sig", signature);
            cmd.Parameters.AddWithValue("@conf", confidence);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@reason", reason);
            await cmd.ExecuteNonQueryAsync(ct);

            // Update entity timestamp
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = "UPDATE entities SET updated_at = @now WHERE entity_id = @id";
            updateCmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            updateCmd.Parameters.AddWithValue("@id", entityId);
            await updateCmd.ExecuteNonQueryAsync(ct);

            _logger.LogInformation("Merged signature {Sig} into entity {Entity} (confidence={Conf:F2}): {Reason}",
                signature[..Math.Min(8, signature.Length)], entityId, confidence, reason);
        }
        finally { _writeLock.Release(); }
    }

    public async Task UpdateEntityAsync(ResolvedEntity entity, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE entities SET
                    updated_at = @now,
                    confidence_level = @level,
                    factor_count = @factors,
                    is_bot = @isBot,
                    bot_probability = @prob,
                    reputation_score = @rep,
                    stable_anchors_json = @anchors,
                    rotation_cadence_seconds = @cadence,
                    velocity_variance = @variance,
                    metadata_json = @meta
                WHERE entity_id = @id
            """;
            cmd.Parameters.AddWithValue("@id", entity.EntityId);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));
            cmd.Parameters.AddWithValue("@level", entity.ConfidenceLevel);
            cmd.Parameters.AddWithValue("@factors", entity.FactorCount);
            cmd.Parameters.AddWithValue("@isBot", entity.IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", entity.BotProbability);
            cmd.Parameters.AddWithValue("@rep", entity.ReputationScore);
            cmd.Parameters.AddWithValue("@anchors", (object?)entity.StableAnchorsJson ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@cadence", entity.RotationCadenceSeconds.HasValue ? entity.RotationCadenceSeconds.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@variance", entity.VelocityVariance.HasValue ? entity.VelocityVariance.Value : DBNull.Value);
            cmd.Parameters.AddWithValue("@meta", (object?)entity.MetadataJson ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { _writeLock.Release(); }
    }

    private static ResolvedEntity ReadEntity(SqliteDataReader reader) => new()
    {
        EntityId = reader.GetString(reader.GetOrdinal("entity_id")),
        CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        UpdatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("updated_at"))),
        ConfidenceLevel = reader.GetInt32(reader.GetOrdinal("confidence_level")),
        FactorCount = reader.GetInt32(reader.GetOrdinal("factor_count")),
        IsBot = reader.GetInt32(reader.GetOrdinal("is_bot")) == 1,
        BotProbability = reader.GetDouble(reader.GetOrdinal("bot_probability")),
        ReputationScore = reader.GetDouble(reader.GetOrdinal("reputation_score")),
        StableAnchorsJson = reader.IsDBNull(reader.GetOrdinal("stable_anchors_json")) ? null : reader.GetString(reader.GetOrdinal("stable_anchors_json")),
        RotationCadenceSeconds = reader.IsDBNull(reader.GetOrdinal("rotation_cadence_seconds")) ? null : reader.GetDouble(reader.GetOrdinal("rotation_cadence_seconds")),
        VelocityVariance = reader.IsDBNull(reader.GetOrdinal("velocity_variance")) ? null : reader.GetDouble(reader.GetOrdinal("velocity_variance")),
        MetadataJson = reader.IsDBNull(reader.GetOrdinal("metadata_json")) ? null : reader.GetString(reader.GetOrdinal("metadata_json"))
    };

    private static EntityEdge ReadEdge(SqliteDataReader reader) => new()
    {
        EdgeId = reader.GetString(reader.GetOrdinal("edge_id")),
        EntityId = reader.GetString(reader.GetOrdinal("entity_id")),
        Signature = reader.GetString(reader.GetOrdinal("signature")),
        EdgeType = Enum.Parse<EntityEdgeType>(reader.GetString(reader.GetOrdinal("edge_type"))),
        Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
        CreatedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("created_at"))),
        Reason = reader.IsDBNull(reader.GetOrdinal("reason")) ? null : reader.GetString(reader.GetOrdinal("reason")),
        RevertedAt = reader.IsDBNull(reader.GetOrdinal("reverted_at")) ? null : DateTime.Parse(reader.GetString(reader.GetOrdinal("reverted_at")))
    };

    public async Task PruneBucketsAsync(TimeSpan retention, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var cutoff = DateTime.UtcNow.Subtract(retention).ToString("O");

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM buckets WHERE bucket_time < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            if (deleted > 0)
                _logger.LogInformation("Pruned {Count} bucket rows older than {Retention}", deleted, retention);
        }
        finally { _writeLock.Release(); }
    }

    public async Task<List<(string Signature, int SessionCount)>> GetOverflowingSignaturesAsync(
        int maxPerSignature, int limit = 500, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT signature, COUNT(*) as cnt
            FROM sessions
            GROUP BY signature
            HAVING cnt > @max
            ORDER BY cnt DESC
            LIMIT @limit
        """;
        cmd.Parameters.AddWithValue("@max", maxPerSignature);
        cmd.Parameters.AddWithValue("@limit", limit);

        var results = new List<(string, int)>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add((reader.GetString(0), reader.GetInt32(1)));
        return results;
    }

    public async Task<CompactionResult> CompactSignatureSessionsAsync(
        string signature, int keepCount, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // Read all sessions for this signature, ordered oldest-first for velocity delta computation
        await using var readConn = new SqliteConnection(_connectionString);
        await readConn.OpenAsync(ct);
        await using var readCmd = readConn.CreateCommand();
        readCmd.CommandText = """
            SELECT id, vector, maturity, ended_at, frequency_fingerprint FROM sessions
            WHERE signature = @sig AND vector IS NOT NULL
            ORDER BY ended_at ASC
        """;
        readCmd.Parameters.AddWithValue("@sig", signature);

        var rows = new List<(long Id, byte[] Vector, float Maturity, DateTime EndedAt, byte[]? FreqFp)>();
        await using var reader = await readCmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(1))
            {
                var freqFpBlob = reader.IsDBNull(4) ? null : (byte[])reader[4];
                rows.Add((reader.GetInt64(0), (byte[])reader[1], reader.GetFloat(2),
                    DateTime.Parse(reader.GetString(3)), freqFpBlob));
            }
        }

        if (rows.Count <= keepCount)
            return new CompactionResult { Signature = signature, CompactedCount = 0 };

        // Rows to compact: all except the most recent keepCount
        var toCompact = rows[..^keepCount];
        var keepIds = rows[^keepCount..].Select(r => r.Id).ToHashSet();

        // Deserialize vectors for compaction
        var vecs = new List<(float[] Vector, float Maturity)>(toCompact.Count);
        var freqFingerprints = new List<float[]>();
        foreach (var (_, rawVec, maturity, _, freqFpBlob) in toCompact)
        {
            var v = DeserializeVector(rawVec);
            if (v != null) vecs.Add((v, maturity));
            var fp = DeserializeVector(freqFpBlob);
            if (fp != null) freqFingerprints.Add(fp);
        }

        if (vecs.Count == 0)
            return new CompactionResult { Signature = signature, CompactedCount = 0 };

        // Maturity-weighted behavioral centroid
        var dims = vecs[0].Vector.Length;
        var centroid = new float[dims];
        double totalWeight = 0;
        foreach (var (vec, mat) in vecs)
        {
            totalWeight += mat;
            for (var i = 0; i < dims; i++)
                centroid[i] += vec[i] * mat;
        }
        if (totalWeight > 0)
            for (var i = 0; i < dims; i++)
                centroid[i] /= (float)totalWeight;

        // Velocity centroid: average of consecutive session deltas (preserves drift direction)
        float[]? velocityCentroid = null;
        if (vecs.Count >= 2)
        {
            var velSum = new float[dims];
            var velCount = 0;
            for (var i = 1; i < vecs.Count; i++)
            {
                var delta = Analysis.SessionVectorizer.ComputeVelocity(vecs[i].Vector, vecs[i - 1].Vector);
                for (var d = 0; d < dims; d++)
                    velSum[d] += delta[d];
                velCount++;
            }
            velocityCentroid = new float[dims];
            for (var d = 0; d < dims; d++)
                velocityCentroid[d] = velSum[d] / velCount;
        }

        var avgMaturity = (float)(totalWeight / vecs.Count);
        var compactedCentroidBytes = SerializeVector(centroid);

        // Frequency fingerprint centroid across compacted sessions
        float[]? freqCentroid = null;
        if (freqFingerprints.Count > 0)
        {
            var fpDims = freqFingerprints[0].Length;
            var fpSum = new float[fpDims];
            foreach (var fp in freqFingerprints)
                for (var i = 0; i < fpDims && i < fp.Length; i++)
                    fpSum[i] += fp[i];
            freqCentroid = fpSum;
            for (var i = 0; i < fpDims; i++) freqCentroid[i] /= freqFingerprints.Count;
        }

        // Update signature root_vector with the compacted centroid, delete old session rows
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var tx = await conn.BeginTransactionAsync(ct);

            // Update root_vector and frequency_centroid on signature
            await using var updateCmd = conn.CreateCommand();
            updateCmd.CommandText = """
                UPDATE signatures
                SET root_vector = @vec, root_vector_maturity = @mat,
                    frequency_centroid = @freqCentroid
                WHERE signature_id = @sig
            """;
            updateCmd.Parameters.AddWithValue("@vec", compactedCentroidBytes);
            updateCmd.Parameters.AddWithValue("@mat", avgMaturity);
            updateCmd.Parameters.AddWithValue("@sig", signature);
            updateCmd.Parameters.AddWithValue("@freqCentroid",
                freqCentroid != null ? (object)SerializeVector(freqCentroid) : DBNull.Value);
            await updateCmd.ExecuteNonQueryAsync(ct);

            // Delete compacted session rows (keep most recent keepCount)
            var idsToDelete = toCompact.Select(r => r.Id).ToList();
            if (idsToDelete.Count > 0)
            {
                await using var delCmd = conn.CreateCommand();
                delCmd.CommandText = $"""
                    DELETE FROM sessions WHERE id IN ({string.Join(",", idsToDelete)})
                """;
                await delCmd.ExecuteNonQueryAsync(ct);
            }

            await tx.CommitAsync(ct);
        }
        finally { _writeLock.Release(); }

        _logger.LogDebug(
            "Compacted {Count} sessions for signature {Sig} (kept {Keep} full-resolution)",
            toCompact.Count, signature[..Math.Min(8, signature.Length)], keepCount);

        return new CompactionResult
        {
            Signature = signature,
            BehavioralCentroid = centroid,
            VelocityCentroid = velocityCentroid,
            FrequencyCentroid = freqCentroid,
            CompactedCount = toCompact.Count,
            CentroidMaturity = avgMaturity
        };
    }

    public async Task<List<CompactionSignatureInfo>> GetSignaturePriorityInfoAsync(
        List<string> signatures, CancellationToken ct = default)
    {
        if (signatures.Count == 0) return [];
        await EnsureInitializedAsync(ct);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();

        // Parameterized IN clause
        var paramNames = signatures.Select((_, i) => $"@p{i}").ToList();
        cmd.CommandText = $"""
            SELECT s.signature_id, COUNT(se.id) as session_count,
                   s.bot_probability, s.risk_band, s.is_bot, s.last_seen,
                   CASE WHEN EXISTS(
                       SELECT 1 FROM entity_edges ee
                       WHERE ee.signature = s.signature_id AND ee.reverted_at IS NULL
                   ) THEN 1 ELSE 0 END as has_entity
            FROM signatures s
            LEFT JOIN sessions se ON se.signature = s.signature_id
            WHERE s.signature_id IN ({string.Join(",", paramNames)})
            GROUP BY s.signature_id
        """;
        for (var i = 0; i < signatures.Count; i++)
            cmd.Parameters.AddWithValue(paramNames[i], signatures[i]);

        var results = new List<CompactionSignatureInfo>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new CompactionSignatureInfo
            {
                Signature = reader.GetString(0),
                SessionCount = reader.GetInt32(1),
                BotProbability = reader.GetDouble(2),
                RiskBand = reader.GetString(3),
                IsBot = reader.GetInt32(4) == 1,
                LastSeen = DateTime.Parse(reader.GetString(5)),
                HasEntityMapping = reader.GetInt32(6) == 1
            });
        }
        return results;
    }

    public async Task PruneAsync(TimeSpan retention, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        var cutoff = DateTime.UtcNow.Subtract(retention).ToString("O");

        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM sessions WHERE ended_at < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff);
            var deleted = await cmd.ExecuteNonQueryAsync(ct);

            await using var cmd2 = conn.CreateCommand();
            cmd2.CommandText = "DELETE FROM buckets WHERE bucket_time < @cutoff";
            cmd2.Parameters.AddWithValue("@cutoff", cutoff);
            await cmd2.ExecuteNonQueryAsync(ct);

            if (deleted > 0)
                _logger.LogInformation("Pruned {Count} sessions older than {Retention}", deleted, retention);
        }
        finally { _writeLock.Release(); }
    }

    // === Helpers ===

    /// <summary>
    ///     Looks up the previous session vector for this signature, computes the velocity delta,
    ///     then adds to the HNSW index with velocity metadata.
    ///     Runs off the write path; failures are silently swallowed.
    /// </summary>
    private async Task AddToVectorSearchAsync(float[] vector, string signature, bool isBot, double botProbability,
        byte[]? frequencyFingerprintBlob = null, byte[]? driftVectorBlob = null)
    {
        try
        {
            float[]? velocityVector = null;

            // Quick lookup of the most recent prior session vector (index-backed: O(log n))
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT vector FROM sessions
                WHERE signature = @sig AND vector IS NOT NULL
                ORDER BY ended_at DESC
                LIMIT 1 OFFSET 1
            """;
            cmd.Parameters.AddWithValue("@sig", signature);
            var raw = await cmd.ExecuteScalarAsync() as byte[];
            if (raw is { Length: > 0 })
            {
                var prevVector = DeserializeVector(raw);
                if (prevVector != null && prevVector.Length == vector.Length)
                    velocityVector = Analysis.SessionVectorizer.ComputeVelocity(vector, prevVector);
            }

            var frequencyFingerprint = DeserializeVector(frequencyFingerprintBlob);
            var driftVector = DeserializeVector(driftVectorBlob);

            await _vectorSearch!.AddAsync(vector, signature, isBot, botProbability,
                velocityVector, frequencyFingerprint, driftVector);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to add session vector to HNSW index (non-fatal)");
        }
    }

    private Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initTask is { IsCompleted: true }) return _initTask;
        return _initTask ??= InitializeAsync(ct);
    }

    private static async Task<List<PersistedSession>> ReadSessionsAsync(SqliteCommand cmd, CancellationToken ct)
    {
        var results = new List<PersistedSession>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
            results.Add(ReadSession(reader));
        return results;
    }

    private static PersistedSession ReadSession(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(reader.GetOrdinal("id")),
        Signature = reader.GetString(reader.GetOrdinal("signature")),
        StartedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("started_at"))),
        EndedAt = DateTime.Parse(reader.GetString(reader.GetOrdinal("ended_at"))),
        RequestCount = reader.GetInt32(reader.GetOrdinal("request_count")),
        Vector = reader.IsDBNull(reader.GetOrdinal("vector")) ? [] : (byte[])reader["vector"],
        Maturity = reader.GetFloat(reader.GetOrdinal("maturity")),
        DominantState = reader.GetString(reader.GetOrdinal("dominant_state")),
        IsBot = reader.GetInt32(reader.GetOrdinal("is_bot")) == 1,
        AvgBotProbability = reader.GetDouble(reader.GetOrdinal("avg_bot_probability")),
        AvgConfidence = reader.GetDouble(reader.GetOrdinal("avg_confidence")),
        RiskBand = reader.GetString(reader.GetOrdinal("risk_band")),
        Action = reader.IsDBNull(reader.GetOrdinal("action")) ? null : reader.GetString(reader.GetOrdinal("action")),
        BotName = reader.IsDBNull(reader.GetOrdinal("bot_name")) ? null : reader.GetString(reader.GetOrdinal("bot_name")),
        BotType = reader.IsDBNull(reader.GetOrdinal("bot_type")) ? null : reader.GetString(reader.GetOrdinal("bot_type")),
        CountryCode = reader.IsDBNull(reader.GetOrdinal("country_code")) ? null : reader.GetString(reader.GetOrdinal("country_code")),
        TopReasonsJson = reader.IsDBNull(reader.GetOrdinal("top_reasons_json")) ? null : reader.GetString(reader.GetOrdinal("top_reasons_json")),
        TransitionCountsJson = reader.IsDBNull(reader.GetOrdinal("transition_counts_json")) ? null : reader.GetString(reader.GetOrdinal("transition_counts_json")),
        PathsJson = reader.IsDBNull(reader.GetOrdinal("paths_json")) ? null : reader.GetString(reader.GetOrdinal("paths_json")),
        AvgProcessingTimeMs = reader.IsDBNull(reader.GetOrdinal("avg_processing_time_ms")) ? 0 : reader.GetDouble(reader.GetOrdinal("avg_processing_time_ms")),
        ErrorCount = reader.IsDBNull(reader.GetOrdinal("error_count")) ? 0 : reader.GetInt32(reader.GetOrdinal("error_count")),
        TimingEntropy = reader.IsDBNull(reader.GetOrdinal("timing_entropy")) ? 0 : reader.GetFloat(reader.GetOrdinal("timing_entropy")),
        Narrative = reader.IsDBNull(reader.GetOrdinal("narrative")) ? null : reader.GetString(reader.GetOrdinal("narrative")),
        UserAgentRaw = SafeGetString(reader, "user_agent_raw"),
        FrequencyFingerprintBlob = SafeGetBytes(reader, "frequency_fingerprint"),
        DriftVectorBlob = SafeGetBytes(reader, "drift_vector")
    };

    /// <summary>Safe column read that handles missing columns (for DBs created before migration).</summary>
    private static string? SafeGetString(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // Column doesn't exist yet (pre-migration DB)
        }
    }

    /// <summary>Safe BLOB column read that handles missing columns (for DBs created before migration).</summary>
    private static byte[]? SafeGetBytes(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : (byte[])reader[ordinal];
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // Column doesn't exist yet (pre-migration DB)
        }
    }

    private static PersistedSignature ReadSignature(SqliteDataReader reader) => new()
    {
        SignatureId = reader.GetString(reader.GetOrdinal("signature_id")),
        SessionCount = reader.GetInt32(reader.GetOrdinal("session_count")),
        TotalRequestCount = reader.GetInt32(reader.GetOrdinal("total_request_count")),
        FirstSeen = DateTime.Parse(reader.GetString(reader.GetOrdinal("first_seen"))),
        LastSeen = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_seen"))),
        IsBot = reader.GetInt32(reader.GetOrdinal("is_bot")) == 1,
        BotProbability = reader.GetDouble(reader.GetOrdinal("bot_probability")),
        Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
        RiskBand = reader.GetString(reader.GetOrdinal("risk_band")),
        BotName = reader.IsDBNull(reader.GetOrdinal("bot_name")) ? null : reader.GetString(reader.GetOrdinal("bot_name")),
        BotType = reader.IsDBNull(reader.GetOrdinal("bot_type")) ? null : reader.GetString(reader.GetOrdinal("bot_type")),
        Action = reader.IsDBNull(reader.GetOrdinal("action")) ? null : reader.GetString(reader.GetOrdinal("action")),
        CountryCode = reader.IsDBNull(reader.GetOrdinal("country_code")) ? null : reader.GetString(reader.GetOrdinal("country_code")),
        RootVector = reader.IsDBNull(reader.GetOrdinal("root_vector")) ? null : (byte[])reader["root_vector"],
        RootVectorMaturity = reader.IsDBNull(reader.GetOrdinal("root_vector_maturity")) ? 0 : reader.GetFloat(reader.GetOrdinal("root_vector_maturity")),
        Narrative = reader.IsDBNull(reader.GetOrdinal("narrative")) ? null : reader.GetString(reader.GetOrdinal("narrative")),
        TopReasonsJson = reader.IsDBNull(reader.GetOrdinal("top_reasons_json")) ? null : reader.GetString(reader.GetOrdinal("top_reasons_json"))
    };

    /// <summary>Deserialize a float[] from a BLOB (IEEE 754 little-endian).</summary>
    /// <summary>Deserialize a BLOB (IEEE 754 little-endian) to float[].</summary>
    public static float[]? DeserializeVector(byte[]? blob)
    {
        if (blob == null || blob.Length == 0) return null;
        var floats = new float[blob.Length / sizeof(float)];
        Buffer.BlockCopy(blob, 0, floats, 0, blob.Length);
        return floats;
    }

    /// <summary>Serialize a float[] to a BLOB (IEEE 754 little-endian).</summary>
    public static byte[] SerializeVector(float[] vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];
        Buffer.BlockCopy(vector, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    public async ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
    }
}