using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Privacy;
using Mostlylucid.BotDetection.RateLimit;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     SQLite-backed dashboard event store for the FOSS product.
///     Zero external dependencies - just a file on disk.
///     Commercial product overrides with PostgreSQL via TryAddSingleton.
/// </summary>
public sealed class SqliteDashboardEventStore : IDashboardEventStore, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly string _connectionString;
    internal string ConnectionString => _connectionString;
    private readonly ILogger<SqliteDashboardEventStore> _logger;
    private readonly TimeSpan _detectionRetention;
    // Startup snapshot of the forgetting-curve blend (FOSS no-reload rule).
    // Defaults (CompressionEnabled=false, weights 0.5/0.3/0.2) apply when the
    // caller didn't bind StyloBotDashboardOptions at all.
    private readonly Configuration.TemporalStoreOptions _temporalStore;
    private readonly double _botFloor;
    private readonly BotDetectionOptions _options;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteDashboardEventStore(
        ILogger<SqliteDashboardEventStore> logger,
        IOptions<BotDetectionOptions> options,
        IOptions<Configuration.StyloBotDashboardOptions>? dashboardOptions = null)
    {
        _logger = logger;
        _options = options.Value;
        _connectionString = DashboardDbPath.GetConnectionString(options.Value);
        // The ONE bot/human cut. Every aggregation below derives is_bot from
        // bot_probability >= @botFloor, never the stored is_bot boolean, so the
        // dashboard can't disagree with the probability. See
        // docs/architecture/bot-human-classification-rationalisation.md.
        _botFloor = options.Value.Classification.BotFloor;
        // dashboardOptions is optional because some callers (notably the
        // detection-only gateway path) don't bind StyloBotDashboardOptions.
        // Default to the historical 7-day retention when absent.
        _detectionRetention = dashboardOptions?.Value.DetectionRetention ?? TimeSpan.FromDays(7);
        _temporalStore = dashboardOptions?.Value.TemporalStore ?? new Configuration.TemporalStoreOptions();
    }

    private async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            // SQLite happily runs the entire multi-statement DDL in one
            // ExecuteNonQuery; if a statement fails, the exception bubbles
            // with the offending line in its message. Same convention as
            // every other SchemaLoader-driven store in the codebase.
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = Data.Schema.UiSchemaLoader.Load("dashboard_events");
                await cmd.ExecuteNonQueryAsync(ct);
            }

            // Migrate: add risk_justification column if absent (idempotent).
            // SQLite does not support IF NOT EXISTS on ALTER TABLE ADD COLUMN;
            // use PRAGMA table_info to check first.
            foreach (var (table, column, colDef) in new (string, string, string)[]
            {
                ("detections", "user_agent_raw", "TEXT"),
                ("detections", "response_bytes", "INTEGER"),
                ("detections", "risk_justification", "TEXT"),
                ("signatures", "risk_justification", "TEXT"),
                ("signatures", "top_reasons_json", "TEXT"),
                ("detections", "domain", "TEXT"),
                // host mirrors domain as the second half of the multi-domain
                // partition key: domain = eTLD+1, host = full lowercased Host
                // header (Task 9 of the multi-domain storage plan). Additive
                // TEXT column so pre-migration rows just render NULL.
                ("detections", "host", "TEXT"),
                ("detections", "referrer_host", "TEXT"),
                ("detections", "ua_device_class", "TEXT"),
                ("detections", "is_verified_bot", "INTEGER DEFAULT 0"),
                // Real origin status (Endpoints UPSTREAM/RETURNED columns). NULL = no real
                // origin call -- honeypot/blocked/throttled traffic never reaches
                // MapReverseProxy, so the gateway's UpstreamStatusTransform never runs for
                // them. That NULL is the meaningful signal, not missing data.
                ("detections", "upstream_status_code", "INTEGER"),
                // Write-time importance weight for the compression fold
                // (DetectionImportance / TemporalStoreOptions). The fold orders
                // the aged region by this and nulls detail columns on
                // low-importance rows; NOT NULL DEFAULT 0 so pre-migration rows
                // are the lowest-importance tier (fold first — correct: they're
                // also the oldest).
                ("detections", "importance_weight", "REAL NOT NULL DEFAULT 0"),
                // Fusion (sparse-aggregate) columns: a fused row is a summary of
                // the low-importance rows it absorbed — one row per
                // (signature, hour-bucket, domain, country, bot_type) carrying
                // the aggregate counters below. fused=1 marks it as NOT a real
                // event: count reads weight it by its counters, drill-downs
                // exclude it. Defaults keep raw rows at fused=0 / hit_count=1
                // with zeroed counters (never read for raw rows).
                ("detections", "fused", "INTEGER NOT NULL DEFAULT 0"),
                ("detections", "hit_count", "INTEGER NOT NULL DEFAULT 1"),
                ("detections", "bot_count", "INTEGER NOT NULL DEFAULT 0"),
                ("detections", "bytes_sum", "INTEGER NOT NULL DEFAULT 0"),
                ("detections", "ms_sum", "REAL NOT NULL DEFAULT 0"),
                ("detections", "ms_max", "REAL NOT NULL DEFAULT 0"),
                // Cache status of the response (write-path grain redesign §3.2 — the
                // minimal endpoint trace: "endpoint, response times, bytes delivered,
                // cache status"). Captured post-_next from the Items marker / X-Cache /
                // CF-Cache-Status; folded into endpoint_stats.cache_status_tally.
                ("detections", "cache_status", "TEXT")
            })
            {
                var colExists = false;
                await using var pragmaCmd = conn.CreateCommand();
                pragmaCmd.CommandText = $"PRAGMA table_info({table})";
                await using (var pr = await pragmaCmd.ExecuteReaderAsync(ct))
                {
                    while (await pr.ReadAsync(ct))
                    {
                        if (string.Equals(pr.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                        { colExists = true; break; }
                    }
                }
                if (!colExists)
                {
                    await using var mc = conn.CreateCommand();
                    mc.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {colDef}";
                    await mc.ExecuteNonQueryAsync(ct);
                }
            }

            // Indexes that depend on the analytics columns added by the migration
            // tuple loop above. Created here so the new columns are guaranteed to
            // exist. Idempotent via IF NOT EXISTS.
            await using (var analyticsCmd = conn.CreateCommand())
            {
                analyticsCmd.CommandText = Data.Schema.UiSchemaLoader.Load("dashboard_events_analytics_indexes");
                await analyticsCmd.ExecuteNonQueryAsync(ct);
            }

            // Backfill ua_device_class for pre-migration rows that have a
            // stripped user_agent_raw value but no derived device class yet.
            // Runs once on startup; idempotent because the WHERE clause restricts
            // to rows where ua_device_class IS NULL.
            await using (var pickCmd = conn.CreateCommand())
            {
                pickCmd.CommandText = """
                    SELECT id, user_agent_raw
                    FROM detections
                    WHERE ua_device_class IS NULL AND user_agent_raw IS NOT NULL
                    """;
                var updates = new List<(long Id, string DeviceClass)>();
                await using (var reader = await pickCmd.ExecuteReaderAsync(ct))
                {
                    while (await reader.ReadAsync(ct))
                    {
                        var id = reader.GetInt64(0);
                        var ua = reader.GetString(1);
                        var deviceClass = Mostlylucid.BotDetection.Helpers.UserAgentParser.ClassifyDeviceClass(ua);
                        if (deviceClass is not null)
                            updates.Add((id, deviceClass));
                    }
                }

                if (updates.Count > 0)
                {
                    await using var tx = conn.BeginTransaction();
                    await using var upd = conn.CreateCommand();
                    upd.Transaction = tx;
                    upd.CommandText = "UPDATE detections SET ua_device_class = @d WHERE id = @id";
                    var pd = upd.CreateParameter(); pd.ParameterName = "@d"; upd.Parameters.Add(pd);
                    var pi = upd.CreateParameter(); pi.ParameterName = "@id"; upd.Parameters.Add(pi);
                    foreach (var (id, dc) in updates)
                    {
                        pd.Value = dc;
                        pi.Value = id;
                        await upd.ExecuteNonQueryAsync(ct);
                    }
                    tx.Commit();
                    _logger.LogInformation(
                        "Backfilled ua_device_class for {Count} pre-migration detections",
                        updates.Count);
                }
            }

            // Prune old detections per StyloBotDashboardOptions.DetectionRetention
            // (default 7 days; configurable per host). Batched: one bounded DELETE
            // per round-trip instead of a single unbounded statement, so the
            // boot-time sweep on a busy database cannot hold a long write lock or
            // bloat the WAL (dbreview- 2026-08-14; the old single DELETE did).
            var pruned = await PruneDetectionsBatchedAsync(
                conn, DateTime.UtcNow.Subtract(_detectionRetention), ct);
            if (pruned > 0) _logger.LogDebug("Pruned {Count} old dashboard detections", pruned);

            // Same retention sweep for the degradation_history table -- the
            // Traffic page never asks for samples older than its widest
            // window (24h today) so anything past the detection retention
            // is dead weight.
            await using var pruneDegradationCmd = conn.CreateCommand();
            pruneDegradationCmd.CommandText = "DELETE FROM degradation_history WHERE timestamp < @cutoff";
            pruneDegradationCmd.Parameters.AddWithValue("@cutoff",
                DateTime.UtcNow.Subtract(_detectionRetention).ToString("O"));
            var prunedDegradation = await pruneDegradationCmd.ExecuteNonQueryAsync(ct);
            if (prunedDegradation > 0) _logger.LogDebug(
                "Pruned {Count} old degradation snapshots", prunedDegradation);

            // Bounded endpoint-stats table — one row per (method, path, domain).
            // Counters are incremented atomically at detection-persist time;
            // GetEndpointStatsAsync reads from here instead of scanning the
            // per-request detections table. Operator directive (2026-08-10):
            // "We do NOT have per-request stores ever. We CAN have endpoints
            // as a limited store."
            await using (var epCmd = conn.CreateCommand())
            {
                epCmd.CommandText = """
                    CREATE TABLE IF NOT EXISTS endpoint_stats (
                        method    TEXT NOT NULL,
                        path      TEXT NOT NULL,
                        domain    TEXT NOT NULL DEFAULT '',
                        is_bot    INTEGER NOT NULL DEFAULT 0,
                        hit_count INTEGER NOT NULL DEFAULT 0,
                        bytes_sum INTEGER NOT NULL DEFAULT 0,
                        ms_sum    REAL    NOT NULL DEFAULT 0,
                        ms_min    REAL    NOT NULL DEFAULT 0,
                        ms_max    REAL    NOT NULL DEFAULT 0,
                        threat_sum REAL   NOT NULL DEFAULT 0,
                        last_seen TEXT    NOT NULL DEFAULT '',
                        s2xx      INTEGER NOT NULL DEFAULT 0,
                        s3xx      INTEGER NOT NULL DEFAULT 0,
                        s4xx      INTEGER NOT NULL DEFAULT 0,
                        s5xx      INTEGER NOT NULL DEFAULT 0,
                        us2xx     INTEGER NOT NULL DEFAULT 0,
                        us3xx     INTEGER NOT NULL DEFAULT 0,
                        us4xx     INTEGER NOT NULL DEFAULT 0,
                        us5xx     INTEGER NOT NULL DEFAULT 0,
                        us_none   INTEGER NOT NULL DEFAULT 0,
                        -- Cache-status tally per endpoint (write-path grain redesign §3.2 —
                        -- the minimal endpoint trace): JSON object {"HIT": 5, "MISS": 2}.
                        -- Legacy rows read as the empty tally.
                        cache_status_tally TEXT NOT NULL DEFAULT '{}',
                        PRIMARY KEY (method, path, domain, is_bot)
                    )
                    """;
                await epCmd.ExecuteNonQueryAsync(ct);

                // The minimal-trace fold columns (ms_min + cache_status_tally) must exist
                // on PRE-EXISTING endpoint_stats tables too. Runs AFTER the CREATE above —
                // the table may predate this code. Same PRAGMA-guarded ALTER pattern as the
                // detections migrations.
                foreach (var (column, colDef) in new (string, string)[]
                         {
                             ("ms_min", "REAL NOT NULL DEFAULT 0"),
                             ("cache_status_tally", "TEXT NOT NULL DEFAULT '{}'"),
                         })
                {
                    var colExists = false;
                    await using var pragmaEp = conn.CreateCommand();
                    pragmaEp.CommandText = "PRAGMA table_info(endpoint_stats)";
                    await using (var pr = await pragmaEp.ExecuteReaderAsync(ct))
                    {
                        while (await pr.ReadAsync(ct))
                        {
                            if (string.Equals(pr.GetString(1), column, StringComparison.OrdinalIgnoreCase))
                            { colExists = true; break; }
                        }
                    }
                    if (!colExists)
                    {
                        await using var mc = conn.CreateCommand();
                        mc.CommandText = $"ALTER TABLE endpoint_stats ADD COLUMN {column} {colDef}";
                        await mc.ExecuteNonQueryAsync(ct);
                    }
                }
            }

            _initialized = true;
            _logger.LogInformation("SQLite dashboard event store initialized at {Path}", _connectionString);
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task AddDetectionAsync(DashboardDetectionEvent detection)
    {
        await EnsureInitializedAsync();
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO detections (timestamp, signature, method, path, is_bot, bot_probability, confidence,
                    risk_band, bot_name, bot_type, action, country_code, processing_time_ms, threat_score, threat_band,
                    status_code, user_agent_raw, risk_justification, domain, host, referrer_host, ua_device_class, response_bytes,
                    is_verified_bot, upstream_status_code, importance_weight, cache_status)
                VALUES (@ts, @sig, @method, @path, @isBot, @prob, @conf, @risk, @name, @type, @action, @country, @ms,
                    @threat, @band, @status, @uaRaw, @justification, @domain, @host, @refHost, @deviceClass, @responseBytes,
                    @verifiedBot, @upstreamStatus, @importance, @cacheStatus)
                """;
            cmd.Parameters.AddWithValue("@ts", detection.Timestamp.ToString("O"));
            cmd.Parameters.AddWithValue("@sig", detection.PrimarySignature ?? "unknown");
            cmd.Parameters.AddWithValue("@method", detection.Method ?? "GET");
            cmd.Parameters.AddWithValue("@path", detection.Path ?? "/");
            cmd.Parameters.AddWithValue("@isBot", detection.IsBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", (double)detection.BotProbability);
            cmd.Parameters.AddWithValue("@conf", (double)detection.Confidence);
            cmd.Parameters.AddWithValue("@risk", detection.RiskBand ?? "Unknown");
            cmd.Parameters.AddWithValue("@name", (object?)detection.BotName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", (object?)detection.BotType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@action", (object?)detection.Action ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", (object?)detection.CountryCode ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@ms", (double)detection.ProcessingTimeMs);
            cmd.Parameters.AddWithValue("@threat", (double)(detection.ThreatScore ?? 0.0));
            cmd.Parameters.AddWithValue("@band", (object?)detection.ThreatBand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@status", (int)detection.StatusCode);
            var strippedUa = UaPiiStripper.Strip(detection.UserAgentRaw);
            cmd.Parameters.AddWithValue("@uaRaw", string.IsNullOrEmpty(strippedUa) ? (object)DBNull.Value : strippedUa);
            cmd.Parameters.AddWithValue("@justification", (object?)detection.RiskJustification ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@domain", (object?)detection.Domain ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@host", (object?)detection.Host ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@refHost", (object?)detection.ReferrerHost ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@deviceClass", (object?)detection.UaDeviceClass ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@responseBytes", (object?)detection.ResponseBytes ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@verifiedBot", detection.IsVerifiedBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@upstreamStatus", (object?)detection.UpstreamStatusCode ?? DBNull.Value);
            // Write-time importance weight (DetectionImportance): computed once
            // here, stored on the row, consumed by the compression fold. The
            // options blend weights are startup-snapshot (FOSS no-reload).
            cmd.Parameters.AddWithValue("@importance",
                DetectionImportance.ComputeWeight(
                    detection.BotProbability,
                    detection.ThreatScore,
                    detection.Action,
                    _temporalStore));
            cmd.Parameters.AddWithValue("@cacheStatus", (object?)detection.CacheStatus ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync();

            // Upsert UA stats for analytics
            await UpsertUserAgentStatsAsync(conn, strippedUa, detection.IsBot);

            // Maintain the bounded endpoint_stats table — operator directive
            // (2026-08-10): endpoint list is a limited store, updated at
            // detection-persist time, never scanned from per-request rows.
            // Internal LAN traffic is excluded from endpoint stats (it's the
            // product's own dashboard/API traffic, not an operator-facing
            // endpoint).
            if (!string.Equals(detection.BotType, "Internal", StringComparison.OrdinalIgnoreCase))
            {
                await UpsertEndpointStatsAsync(
                    detection.Method ?? "GET",
                    detection.Path ?? "/",
                    detection.Domain,
                    detection.IsBot,
                    detection.BotProbability,
                    detection.ThreatScore,
                    detection.ResponseBytes,
                    detection.ProcessingTimeMs,
                    detection.StatusCode,
                    detection.UpstreamStatusCode,
                    detection.Timestamp,
                    detection.CacheStatus);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
    {
        await EnsureInitializedAsync();
        await _writeLock.WaitAsync();
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync();

            await using var cmd = conn.CreateCommand();
            // top_reasons_json carries the per-detection reasons / synthesized
            // positive-signal summary that powers the "Detection Signals" panel
            // on the signature detail page. The column was migrated in but the
            // INSERT didn't list it, so every row wrote NULL and every detail
            // page rendered "No detection signals recorded". Storing the JSON
            // here and the read path's `s.top_reasons_json` projection now
            // returns the actual reasons the operator needs to read.
            cmd.CommandText = """
                INSERT INTO signatures (signature, bot_name, bot_type, is_bot, bot_probability, confidence,
                    risk_band, action, country_code, hit_count, first_seen, last_seen, processing_time_ms,
                    threat_score, threat_band, narrative, risk_justification, top_reasons_json)
                VALUES (@sig, @name, @type, @isBot, @prob, @conf, @risk, @action, @country, 1, @now, @now, @ms,
                    @threat, @band, @narrative, @justification, @reasons)
                ON CONFLICT(signature) DO UPDATE SET
                    bot_name = COALESCE(@name, bot_name),
                    bot_type = COALESCE(@type, bot_type),
                    is_bot = @isBot,
                    bot_probability = @prob,
                    confidence = @conf,
                    risk_band = @risk,
                    action = @action,
                    country_code = COALESCE(@country, country_code),
                    hit_count = hit_count + 1,
                    last_seen = @now,
                    processing_time_ms = @ms,
                    threat_score = @threat,
                    threat_band = @band,
                    narrative = COALESCE(@narrative, narrative),
                    -- risk_justification tracks the CURRENT band's explanation, not
                    -- the first one ever recorded. The earlier COALESCE froze the
                    -- justification on first write so the signature detail page
                    -- showed a stale reason string that no longer matched the band.
                    risk_justification = @justification,
                    -- top_reasons_json: always overwrite with the latest -- this is the
                    -- live decision surface the operator reads, not a first-write
                    -- audit log. The first-write trail lives in the detections table.
                    top_reasons_json = COALESCE(@reasons, top_reasons_json)
                RETURNING hit_count
                """;
            cmd.Parameters.AddWithValue("@sig", signature.PrimarySignature ?? "unknown");
            cmd.Parameters.AddWithValue("@name", (object?)signature.BotName ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@type", (object?)signature.BotType ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@isBot", signature.IsKnownBot ? 1 : 0);
            cmd.Parameters.AddWithValue("@prob", (double)(signature.BotProbability ?? 0));
            cmd.Parameters.AddWithValue("@conf", (double)(signature.Confidence ?? 0));
            cmd.Parameters.AddWithValue("@risk", signature.RiskBand ?? "Unknown");
            cmd.Parameters.AddWithValue("@action", (object?)signature.Action ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@country", DBNull.Value);
            cmd.Parameters.AddWithValue("@ms", (double)(signature.ProcessingTimeMs ?? 0));
            cmd.Parameters.AddWithValue("@threat", (double)(signature.ThreatScore ?? 0));
            cmd.Parameters.AddWithValue("@band", (object?)signature.ThreatBand ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@narrative", (object?)signature.Narrative ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@justification", (object?)signature.RiskJustification ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@reasons",
                signature.TopReasons is { Count: > 0 } reasons
                    ? (object)System.Text.Json.JsonSerializer.Serialize(reasons)
                    : DBNull.Value);
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

            var hitCount = await cmd.ExecuteScalarAsync();
            return signature with { HitCount = Convert.ToInt32(hitCount ?? 1) };
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpdateSignatureBotNameAsync(string signature, string name, string? description, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(signature) || string.IsNullOrEmpty(name)) return;
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                UPDATE signatures
                   SET bot_name = @name,
                       narrative = COALESCE(@desc, narrative)
                 WHERE signature = @sig
                """;
            cmd.Parameters.AddWithValue("@sig", signature);
            cmd.Parameters.AddWithValue("@name", name);
            cmd.Parameters.AddWithValue("@desc", (object?)description ?? DBNull.Value);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // LEFT JOIN signatures so the signature-level top_reasons_json
        // (live "Detection Signals" decision surface) rides along on every
        // detection row. Detections themselves don't store reasons (they're
        // per-request audit rows); the signatures row carries the latest
        // synthesised+contribution reasons that the signature detail page
        // and any per-request inspector renders.
        // Drill-down: fused summary rows are NOT real events — exclude them.
        var sql = "SELECT d.*, s.top_reasons_json AS top_reasons_json FROM detections d LEFT JOIN signatures s ON d.signature = s.signature";
        var conditions = new List<string> { "d.fused = 0" };
        await using var cmd = conn.CreateCommand();

        if (filter?.StartTime.HasValue == true)
        {
            conditions.Add("d.timestamp >= @start");
            cmd.Parameters.AddWithValue("@start", filter.StartTime.Value.ToString("O"));
        }
        if (filter?.EndTime.HasValue == true)
        {
            conditions.Add("d.timestamp <= @end");
            cmd.Parameters.AddWithValue("@end", filter.EndTime.Value.ToString("O"));
        }
        if (filter?.IsBot.HasValue == true)
        {
            conditions.Add("d.is_bot = @isBot");
            cmd.Parameters.AddWithValue("@isBot", filter.IsBot.Value ? 1 : 0);
        }
        if (!string.IsNullOrEmpty(filter?.SignatureId))
        {
            conditions.Add("d.signature = @sig");
            cmd.Parameters.AddWithValue("@sig", filter.SignatureId);
        }
        if (filter?.Domains is { Count: > 0 })
        {
            var (domainPred, domainParams) = BuildDomainPredicate(filter.Domains, "d");
            conditions.Add(domainPred);
            foreach (var (name, value) in domainParams)
                cmd.Parameters.AddWithValue(name, value);
        }

        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);

        sql += " ORDER BY d.timestamp DESC LIMIT @limit";
        cmd.Parameters.AddWithValue("@limit", filter?.Limit ?? 100);
        cmd.CommandText = sql;

        var results = new List<DashboardDetectionEvent>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        // Resolve ordinals once before the loop — GetOrdinal does a linear name scan per call.
        var ordTimestamp    = reader.GetOrdinal("timestamp");
        var ordSignature    = reader.GetOrdinal("signature");
        var ordMethod       = reader.GetOrdinal("method");
        var ordPath         = reader.GetOrdinal("path");
        var ordIsBot        = reader.GetOrdinal("is_bot");
        var ordProb         = reader.GetOrdinal("bot_probability");
        var ordConf         = reader.GetOrdinal("confidence");
        var ordRisk         = reader.GetOrdinal("risk_band");
        var ordBotName      = reader.GetOrdinal("bot_name");
        var ordBotType      = reader.GetOrdinal("bot_type");
        var ordAction       = reader.GetOrdinal("action");
        var ordCountry      = reader.GetOrdinal("country_code");
        var ordProcMs       = reader.GetOrdinal("processing_time_ms");
        var ordThreat       = reader.GetOrdinal("threat_score");
        var ordThreatBand   = reader.GetOrdinal("threat_band");
        var ordStatus       = reader.GetOrdinal("status_code");
        var ordDomain       = reader.GetOrdinal("domain");
        var ordRefHost      = reader.GetOrdinal("referrer_host");
        var ordDeviceClass  = reader.GetOrdinal("ua_device_class");

        while (await reader.ReadAsync(ct))
        {
            results.Add(new DashboardDetectionEvent
            {
                Timestamp       = DateTime.Parse(reader.GetString(ordTimestamp)),
                PrimarySignature = reader.GetString(ordSignature),
                RequestId       = reader.GetString(ordSignature),
                Method          = reader.IsDBNull(ordMethod)  ? "" : reader.GetString(ordMethod),
                Path            = reader.IsDBNull(ordPath)    ? "/" : reader.GetString(ordPath),
                IsBot           = reader.GetInt32(ordIsBot) == 1,
                BotProbability  = reader.GetDouble(ordProb),
                Confidence      = reader.GetDouble(ordConf),
                RiskBand        = reader.IsDBNull(ordRisk)     ? "Unknown" : reader.GetString(ordRisk),
                BotName         = reader.IsDBNull(ordBotName)  ? null : reader.GetString(ordBotName),
                BotType         = reader.IsDBNull(ordBotType)  ? null : reader.GetString(ordBotType),
                Action          = reader.IsDBNull(ordAction)   ? null : reader.GetString(ordAction),
                CountryCode     = reader.IsDBNull(ordCountry)  ? null : reader.GetString(ordCountry),
                ProcessingTimeMs = reader.GetDouble(ordProcMs),
                ThreatScore     = reader.GetDouble(ordThreat),
                ThreatBand      = reader.IsDBNull(ordThreatBand) ? null : reader.GetString(ordThreatBand),
                StatusCode      = reader.GetInt32(ordStatus),
                UserAgentRaw    = SafeGetString(reader, "user_agent_raw"),
                RiskJustification = SafeGetString(reader, "risk_justification"),
                ResponseBytes   = SafeGetInt64Nullable(reader, "response_bytes"),
                Domain          = reader.IsDBNull(ordDomain)      ? null : reader.GetString(ordDomain),
                ReferrerHost    = reader.IsDBNull(ordRefHost)      ? null : reader.GetString(ordRefHost),
                UaDeviceClass   = reader.IsDBNull(ordDeviceClass)  ? null : reader.GetString(ordDeviceClass),
                // SafeGet form so older DBs that haven't run the is_verified_bot
                // migration yet (e.g. during the rollout window before
                // EnsureInitializedAsync has run the ALTER TABLE) read as false
                // rather than throwing on missing column.
                IsVerifiedBot   = SafeGetInt32(reader, "is_verified_bot") == 1,
                // top_reasons_json rides along from the JOIN'd signatures row.
                // Deserialises to the same List<string> shape DashboardDetectionEvent
                // exposes; null when the row hasn't been synthesised yet (or the
                // join missed -- legacy pre-migration rows).
                TopReasons      = ParseTopReasonsJson(SafeGetString(reader, "top_reasons_json")) ?? new List<string>(),
            });
        }
        return results;
    }

    /// <summary>
    ///     Deserialises top_reasons_json column to a list, swallowing malformed
    ///     payloads. Single chokepoint so the read path's null-handling is
    ///     consistent across GetDetectionsAsync / GetTopBotsAsync.
    /// </summary>
    private static List<string>? ParseTopReasonsJson(string? json)
    {
        if (string.IsNullOrEmpty(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json); }
        catch { return null; }
    }

    public async Task<DashboardSignatureEvent?> TryGetSignatureAsync(
        string signatureId, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT * FROM signatures WHERE signature = @sig LIMIT 1";
        cmd.Parameters.AddWithValue("@sig", signatureId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (await reader.ReadAsync(ct))
            return ReadSignature(reader);
        return null;
    }

    public async Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100, int offset = 0, bool? isBot = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "SELECT * FROM signatures";
        if (isBot.HasValue) sql += " WHERE is_bot = @isBot";
        sql += " ORDER BY last_seen DESC LIMIT @limit OFFSET @offset";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        if (isBot.HasValue) cmd.Parameters.AddWithValue("@isBot", isBot.Value ? 1 : 0);
        cmd.Parameters.AddWithValue("@limit", limit);
        cmd.Parameters.AddWithValue("@offset", offset);

        var results = new List<DashboardSignatureEvent>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(ReadSignature(reader));
        }
        return results;
    }

    /// <summary>
    ///     Audience predicate for the <c>detections</c> table, mirroring the commercial
    ///     <c>PostgreSQLDashboardEventStore.AudiencePredicate</c> so dev (SQLite) and prod
    ///     (Postgres) agree. Default EXCLUDES self-traffic (<c>bot_type='Internal'</c>:
    ///     loopback / RFC1918 / health probes); "internal" shows only self; "all" shows the
    ///     full mix; "humans"/"bots" apply the bot-probability floor gate AND exclude self.
    ///     "all_incl_internal" is the explicit opt-in token backing the dashboard's "Show
    ///     self-probe" toggle (same predicate as "all" here — no exclusion, no gate; kept as
    ///     a distinct, self-documenting token so it means the same thing across every
    ///     AudiencePredicate/ComposeAudiencePredicate switch in both the FOSS SQLite and
    ///     commercial Postgres backends). SQLite <c>IS NOT</c> is null-safe, so NULL-<c>bot_type</c>
    ///     humans are kept (matches Postgres <c>IS DISTINCT FROM</c>). Callers bind
    ///     <c>@botFloor</c> for humans/bots.
    /// </summary>
    private static string AudiencePredicate(string? audienceFilter) => audienceFilter?.ToLowerInvariant() switch
    {
        "internal"          => " AND bot_type = 'Internal'",
        "all"               => string.Empty,
        "all_incl_internal" => string.Empty,
        "bots"              => " AND bot_probability >= @botFloor AND bot_type IS NOT 'Internal'",
        "humans"            => " AND bot_probability < @botFloor AND bot_type IS NOT 'Internal'",
        _                   => " AND bot_type IS NOT 'Internal'",
    };

    // ---- Fused-row aggregation fragments -----------------------------------
    // A fused row is a (signature, hour, domain, country, bot_type) summary of
    // the low-importance rows it absorbed: it is NOT a real event, so every
    // count aggregation must weight it by its counters, and drill-downs must
    // exclude it (fused = 0). Fused rows always pass audience WHERE filters —
    // their bot/human split lives in the counters — so aggregate queries wrap
    // the raw audience predicate: "(fused = 1 OR <raw predicate>)".
    private const string FusedTotalExpr   = "CASE WHEN fused = 1 THEN hit_count ELSE 1 END";
    private const string FusedBytesExpr   = "CASE WHEN fused = 1 THEN bytes_sum ELSE COALESCE(response_bytes, 0) END";
    private const string FusedMsSumExpr   = "CASE WHEN fused = 1 THEN ms_sum ELSE COALESCE(processing_time_ms, 0) END";
    private const string FusedMsMaxExpr   = "CASE WHEN fused = 1 THEN ms_max ELSE processing_time_ms END";

    /// <summary>
    ///     WHERE clause that lets fused rows through regardless of audience —
    ///     the raw audience predicate applies to raw rows; the fused row's own
    ///     split is applied in the SELECT via the counter expressions.
    /// </summary>
    private static string FusedAudienceWhere(string? audienceFilter)
    {
        var raw = AudiencePredicate(audienceFilter);
        if (raw.Length == 0) return string.Empty;
        var body = raw.Substring(" AND ".Length);
        return $" AND (fused = 1 OR ({body}))";
    }

    /// <summary>
    ///     Bot-count expression for fused rows, audience-aware: for "bots" the
    ///     fused row contributes its bot_count and the raw rows were pre-filtered
    ///     to bots (count 1 each); for "humans" the fused row contributes 0 (its
    ///     human portion counts via <see cref="FusedTotalExprFor"/>'s humans
    ///     variant); otherwise the raw per-row floor CASE.
    /// </summary>
    private static string FusedBotsExpr(string? audienceFilter) => audienceFilter?.ToLowerInvariant() switch
    {
        "humans" => "CASE WHEN fused = 1 THEN 0 WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END",
        "bots"   => "CASE WHEN fused = 1 THEN bot_count ELSE 1 END",
        _        => "CASE WHEN fused = 1 THEN bot_count WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END",
    };

    /// <summary>
    ///     Total-count expression, audience-aware: fused rows contribute their
    ///     full hit_count for unfiltered reads, their bot/human portion for
    ///     audience-filtered reads (the raw rows were pre-filtered, count 1).
    /// </summary>
    private static string FusedTotalExprFor(string? audienceFilter) => audienceFilter?.ToLowerInvariant() switch
    {
        "bots"   => "CASE WHEN fused = 1 THEN bot_count ELSE 1 END",
        "humans" => "CASE WHEN fused = 1 THEN hit_count - bot_count ELSE 1 END",
        _        => FusedTotalExpr,
    };

    public async Task<DashboardSummary> GetSummaryAsync(
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? audienceFilter = null,
        IReadOnlyList<string>? domains = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // When no window is provided, preserve legacy 6-hour default.
        var sinceStr = (startTime ?? DateTime.UtcNow.AddHours(-6)).ToString("O");
        var untilStr = (endTime ?? DateTime.MaxValue).ToString("O");
        var hasUntil = endTime.HasValue;

        // Internal-exclusion + bot/human gate, shared with Postgres (see AudiencePredicate).
        // Fused rows always pass the WHERE; their split applies via the counter
        // expressions in the SELECT (see FusedAudienceWhere / FusedBotsExpr).
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);

        // Request-level counts (one detection row per request — drives traffic charts).
        // Also aggregates bytes_out, avg/max processing time for the KPI strip.
        int total = 0, bots = 0;
        long bytesOut = 0;
        double avgMs = 0.0, maxMs = 0.0;

        await using (var cmd = conn.CreateCommand())
        {
            var untilClause = hasUntil ? " AND timestamp < @until" : string.Empty;
            var totalExpr = FusedTotalExprFor(audienceFilter);
            var botsExpr = FusedBotsExpr(audienceFilter);
            cmd.CommandText = $"""
                SELECT
                    SUM({totalExpr}) AS total,
                    SUM({botsExpr}) AS bots,
                    SUM({FusedBytesExpr}) AS bytes_out,
                    SUM({FusedMsSumExpr}) / NULLIF(SUM({totalExpr}), 0) AS avg_ms,
                    MAX({FusedMsMaxExpr}) AS max_ms
                FROM detections
                WHERE timestamp >= @since{untilClause}{FusedAudienceWhere(audienceFilter)}{domainPredicate}
                """;
            cmd.Parameters.AddWithValue("@since", sinceStr);
            cmd.Parameters.AddWithValue("@botFloor", _botFloor);
            if (hasUntil) cmd.Parameters.AddWithValue("@until", untilStr);
            foreach (var (name, value) in domainParams) cmd.Parameters.AddWithValue(name, value);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                total    = reader.IsDBNull(0) ? 0  : reader.GetInt32(0);
                bots     = reader.IsDBNull(1) ? 0  : reader.GetInt32(1);
                bytesOut = reader.IsDBNull(2) ? 0L : Convert.ToInt64(reader.GetValue(2));
                avgMs    = reader.IsDBNull(3) ? 0.0 : Convert.ToDouble(reader.GetValue(3));
                maxMs    = reader.IsDBNull(4) ? 0.0 : Convert.ToDouble(reader.GetValue(4));
            }
        }

        // SQLite lacks PERCENTILE_CONT — approximation; Postgres backend returns true PERCENTILE_CONT.
        var p95Ms = avgMs + (maxMs - avgMs) * 0.9;

        // Fingerprint-level counts (one signatures row per unique fingerprint).
        // bot_probability is the EWMA-blended posterior, risk_band is the latest
        // band — so these are the "how many actors did we see" counts the
        // dashboard banner should be showing.
        // The audience filter does NOT apply here — fingerprint counts are identity-level,
        // not request-level, and remain unfiltered regardless of the audience parameter.
        int sigs = 0, botSigs = 0, humanSigs = 0, highSigs = 0;
        var riskBands = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COUNT(*) AS sigs,
                    SUM(CASE WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END) AS bot_sigs,
                    SUM(CASE WHEN bot_probability < @botFloor THEN 1 ELSE 0 END) AS human_sigs,
                    SUM(CASE WHEN risk_band IN ('High','VeryHigh') THEN 1 ELSE 0 END) AS high_sigs
                FROM signatures
                WHERE last_seen >= @since
                """;
            cmd.Parameters.AddWithValue("@since", sinceStr);
            cmd.Parameters.AddWithValue("@botFloor", _botFloor);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                sigs      = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                botSigs   = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                humanSigs = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
                highSigs  = reader.IsDBNull(3) ? 0 : reader.GetInt32(3);
            }
        }

        // Risk-band distribution at fingerprint level (one bucket per signature).
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT COALESCE(risk_band, 'Unknown') AS band, COUNT(*) AS n
                FROM signatures
                WHERE last_seen >= @since
                GROUP BY band
                """;
            cmd.Parameters.AddWithValue("@since", sinceStr);
            cmd.Parameters.AddWithValue("@botFloor", _botFloor);
            await using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var band = reader.IsDBNull(0) ? "Unknown" : reader.GetString(0);
                var n = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
                riskBands[band] = n;
            }
        }

        return new DashboardSummary
        {
            Timestamp = DateTime.UtcNow,
            TotalRequests = total,
            BotRequests = bots,
            HumanRequests = total - bots,
            UncertainRequests = 0,
            UniqueSignatures = sigs,
            BotFingerprints = botSigs,
            HumanFingerprints = humanSigs,
            HighRiskFingerprints = highSigs,
            RiskBandCounts = riskBands,
            TopBotTypes = new Dictionary<string, int>(),
            TopActions = new Dictionary<string, int>(),
            BytesOut = bytesOut,
            AverageProcessingTimeMs = Math.Round(avgMs, 2),
            P95ProcessingTimeMs = Math.Round(p95Ms, 2),
            MaxProcessingTimeMs = Math.Round(maxMs, 2)
        };
    }

    public async Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime,
        DateTime endTime,
        TimeSpan bucketSize,
        string? audienceFilter = null,
        IReadOnlyList<string>? domains = null)
    {
        await EnsureInitializedAsync();

        // True N-second bucketing via integer-division on unix epoch. The old branched
        // strftime path hardcoded minute precision for sub-hour bucketSize values, so a
        // request for 5-minute buckets returned per-minute rows and the gap-fill loop
        // (which iterated by bucketSize but looked up minute keys) produced an
        // overlapping/scrambled series the chart drew as disconnected points.
        var bucketSeconds = Math.Max((int)bucketSize.TotalSeconds, 1);

        // Internal-exclusion + bot/human gate, shared with Postgres (see AudiencePredicate).
        // Fused rows always pass the WHERE; their split applies via the counter
        // expressions in the SELECT. Fused rows' timestamps are hour-bucket
        // anchored, so the bucket math below lands them in their own bucket.
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                strftime('%Y-%m-%dT%H:%M:%SZ',
                         (CAST(strftime('%s', timestamp) AS INTEGER) / @bucket) * @bucket,
                         'unixepoch') AS bucket,
                SUM({FusedBotsExpr(audienceFilter)}) AS bots,
                SUM({FusedTotalExprFor(audienceFilter)}) - SUM({FusedBotsExpr(audienceFilter)}) AS humans,
                SUM({FusedTotalExprFor(audienceFilter)}) AS total,
                SUM({FusedBytesExpr}) AS bytes_out,
                SUM({FusedMsSumExpr}) / NULLIF(SUM({FusedTotalExprFor(audienceFilter)}), 0) AS avg_ms,
                MAX({FusedMsMaxExpr}) AS max_ms
            FROM detections
            WHERE timestamp >= @start AND timestamp < @end{FusedAudienceWhere(audienceFilter)}{domainPredicate}
            GROUP BY bucket
            ORDER BY bucket
            """;
        cmd.Parameters.AddWithValue("@start", startTime.ToString("O"));
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        cmd.Parameters.AddWithValue("@end", endTime.ToString("O"));
        cmd.Parameters.AddWithValue("@bucket", bucketSeconds);
        foreach (var (name, value) in domainParams) cmd.Parameters.AddWithValue(name, value);

        var dbPoints = new Dictionary<string, DashboardTimeSeriesPoint>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var bucket = reader.GetString(0);
            if (DateTime.TryParse(bucket, System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.RoundtripKind, out var ts))
            {
                var bucketAvgMs = reader.IsDBNull(5) ? 0.0 : Convert.ToDouble(reader.GetValue(5));
                var bucketMaxMs = reader.IsDBNull(6) ? 0.0 : Convert.ToDouble(reader.GetValue(6));
                // SQLite lacks PERCENTILE_CONT — approximation; Postgres backend returns true PERCENTILE_CONT.
                var bucketP95Ms = bucketAvgMs + (bucketMaxMs - bucketAvgMs) * 0.9;

                dbPoints[bucket] = new DashboardTimeSeriesPoint
                {
                    Timestamp = ts,
                    BotCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    HumanCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    TotalCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3),
                    BytesOut = reader.IsDBNull(4) ? 0L : Convert.ToInt64(reader.GetValue(4)),
                    AvgProcessingTimeMs = Math.Round(bucketAvgMs, 2),
                    P95ProcessingTimeMs = Math.Round(bucketP95Ms, 2),
                    MaxProcessingTimeMs = Math.Round(bucketMaxMs, 2)
                };
            }
        }

        // Align startTime DOWN to the nearest bucket boundary so the gap-fill iteration
        // produces keys that exactly match the SQL output's "yyyy-MM-ddTHH:mm:ssZ" format.
        var startUtc = startTime.ToUniversalTime();
        var alignedTicks = (startUtc.Ticks / bucketSize.Ticks) * bucketSize.Ticks;
        var current = new DateTime(alignedTicks, DateTimeKind.Utc);

        var points = new List<DashboardTimeSeriesPoint>();
        while (current < endTime)
        {
            var key = current.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            points.Add(dbPoints.TryGetValue(key, out var p) ? p : new DashboardTimeSeriesPoint
            {
                Timestamp = current,
                BotCount = 0,
                HumanCount = 0,
                TotalCount = 0,
                BytesOut = 0L,
                AvgProcessingTimeMs = 0.0,
                P95ProcessingTimeMs = 0.0,
                MaxProcessingTimeMs = 0.0
            });
            current = current.Add(bucketSize);
        }
        return points;
    }

    /// <summary>
    ///     True when a <c>sessions</c> table exists in this connection's database. The last_path
    ///     enrichment in <see cref="GetTopBotsAsync"/> reads it; co-located deployments (website /
    ///     all-in-one) share one DB file so it is present, but the stylobot gateway with --enable-api
    ///     keeps dashboard.db and sessions.db apart, so it is not. A hard reference would 500 topbots.
    /// </summary>
    private static async Task<bool> SessionsTableExistsAsync(SqliteConnection conn)
    {
        await using var probe = conn.CreateCommand();
        probe.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'sessions' LIMIT 1";
        return await probe.ExecuteScalarAsync() is not null;
    }

    // NOTE: The last_path column reads the 'sessions' table created by SqliteDetectionArchive (core
    // package) WHEN the session store shares this database file (website / all-in-one). The stylobot
    // gateway (--enable-api) keeps the stores in separate files, so last_path degrades to NULL there
    // via SessionsTableExistsAsync — either way reader.IsDBNull(12) handles the NULL.
    // top_reasons_json is migrated by EnsureInitializedAsync — absent on pre-migration DBs it reads NULL.
    // Column order (0-based): signature(0), bot_name(1), bot_type(2), bot_probability(3), hit_count(4),
    //   last_seen(5), threat_score(6), threat_band(7), action(8), narrative(9), top_reasons_json(10),
    //   country_code(11), last_path(12), bytes_out(13)
    public async Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
    {
        // Audience semantics:
        //   null / "bots" -- bots only (back-compat: this was the original hardcoded behaviour
        //                    when the only caller was the "Top Bots" widget)
        //   "humans"      -- humans only (signature.is_bot = 0)
        //   "all"         -- bots + humans, no is_bot predicate (used by callers that need a
        //                    cross-cutting top-N for accurate audience counts)
        // Same audience semantics as AudiencePredicate but against the signatures table
        // (alias s, WHERE form): default is bots-only + exclude self, "internal" shows only
        // self, "all" shows the full mix. Keeps dev consistent with Postgres + the windowed path.
        var isBotPredicate = audienceFilter?.ToLowerInvariant() switch
        {
            "internal" => "WHERE s.bot_type = 'Internal'",
            "all"      => string.Empty,
            "humans"   => "WHERE s.bot_probability < @botFloor AND s.bot_type IS NOT 'Internal'",
            _          => "WHERE s.bot_probability >= @botFloor AND s.bot_type IS NOT 'Internal'"
        };

        // When a time window is specified, or a domain filter is applied, aggregate
        // directly from detections so the hit counts honour the window boundary and
        // the domain scope instead of returning all-time / all-domain totals.
        var hasDomainFilter = domains is { Count: > 0 };
        if (startTime.HasValue || endTime.HasValue || hasDomainFilter)
            return await GetTopBotsWindowedAsync(count, startTime, endTime, audienceFilter, domains);

        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // The last_path enrichment reads the session store's `sessions` table. When the
        // dashboard event store and the session store are separate SQLite files -- the
        // stylobot gateway with --enable-api keeps dashboard.db and sessions.db apart --
        // `sessions` is not in this connection's database, and a hard reference fails the
        // whole query with "no such table: sessions" (topbots then 500s). Probe for it and
        // degrade last_path to NULL when absent; co-located deployments keep the enrichment.
        var lastPathExpr = await SessionsTableExistsAsync(conn)
            ? """
              (SELECT json_extract(ses.paths_json, '$[0]')
                    FROM sessions ses
                    WHERE ses.signature = s.signature AND ses.paths_json IS NOT NULL
                    ORDER BY ses.ended_at DESC
                    LIMIT 1)
              """
            : "NULL";

        await using var cmd = conn.CreateCommand();
        // bytes_out is computed over ALL detections for this signature (no time filter).
        // This is the all-time / cache-seed path — windowed calls go to GetTopBotsWindowedAsync.
        cmd.CommandText = $"""
            SELECT s.signature, s.bot_name, s.bot_type, s.bot_probability, s.hit_count, s.last_seen,
                   s.threat_score, s.threat_band, s.action, s.narrative, s.top_reasons_json, s.country_code,
                   {lastPathExpr} AS last_path,
                   COALESCE((SELECT SUM(d.response_bytes) FROM detections d WHERE d.signature = s.signature), 0) AS bytes_out,
                   (s.bot_probability >= @botFloor) AS is_bot
            FROM signatures s
            {isBotPredicate}
            ORDER BY s.hit_count DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);

        var results = new List<DashboardTopBotEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            List<string>? topReasons = null;
            if (!reader.IsDBNull(10))
            {
                try { topReasons = System.Text.Json.JsonSerializer.Deserialize<List<string>>(reader.GetString(10)); }
                catch { /* ignore malformed json */ }
            }
            results.Add(new DashboardTopBotEntry
            {
                PrimarySignature = reader.GetString(0),
                BotName = reader.IsDBNull(1) ? null : reader.GetString(1),
                BotType = reader.IsDBNull(2) ? null : reader.GetString(2),
                BotProbability = reader.GetDouble(3),
                HitCount = reader.GetInt32(4),
                LastSeen = DateTime.Parse(reader.GetString(5)),
                ThreatScore = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                ThreatBand = reader.IsDBNull(7) ? null : reader.GetString(7),
                Action = reader.IsDBNull(8) ? null : reader.GetString(8),
                Narrative = reader.IsDBNull(9) ? null : reader.GetString(9),
                TopReasons = topReasons,
                CountryCode = reader.IsDBNull(11) ? null : reader.GetString(11),
                LastPath = reader.IsDBNull(12) ? null : reader.GetString(12),
                BytesOut = reader.IsDBNull(13) ? 0L : Convert.ToInt64(reader.GetValue(13)),
                IsKnownBot = reader.GetInt32(14) == 1,
            });
        }
        return results;
    }

    public async Task<FilterCounts> GetVisitorSegmentCountsAsync(
        DateTime startTime, DateTime endTime,
        string? filter = null, string? country = null, string? botType = null,
        string? threat = null, IReadOnlyList<string>? domains = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Build WHERE clause for filters
        var whereParts = new List<string>
        {
            "d.timestamp >= @startTime AND d.timestamp <= @endTime"
        };

        if (domains is { Count: > 0 })
        {
            whereParts.Add($"d.domain IN ({string.Join(",", domains.Select((_, i) => $"@domain{i}"))})");
        }
        if (!string.IsNullOrEmpty(country))
            whereParts.Add("s.country_code = @country");
        if (!string.IsNullOrEmpty(botType))
            whereParts.Add("s.bot_type = @botType");
        if (!string.IsNullOrEmpty(threat))
            whereParts.Add("CASE WHEN s.threat_band = 'Critical' OR s.threat_band = 'High' THEN 3 WHEN s.threat_band = 'Elevated' OR s.threat_band = 'Medium' THEN 2 WHEN s.threat_band = 'Low' THEN 1 ELSE 0 END >= @threatRank");

        var whereClause = string.Join(" AND ", whereParts);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
              COUNT(DISTINCT d.signature) as all_count,
              COUNT(DISTINCT CASE WHEN s.bot_probability < @botFloor AND s.bot_type IS NOT 'Internal' THEN d.signature END) as humans,
              COUNT(DISTINCT CASE WHEN s.bot_probability >= @botFloor AND s.bot_type IS NOT 'Internal' THEN d.signature END) as bots,
              COUNT(DISTINCT CASE WHEN s.bot_probability >= @botFloor AND s.bot_type LIKE 'AI%' THEN d.signature END) as ai,
              COUNT(DISTINCT CASE WHEN s.bot_probability >= @botFloor AND s.bot_type LIKE 'Search%' THEN d.signature END) as search,
              COUNT(DISTINCT CASE WHEN s.bot_probability >= @botFloor AND (s.bot_type LIKE 'Tool%' OR s.bot_type = 'Tools') THEN d.signature END) as tools,
              COUNT(DISTINCT CASE WHEN s.bot_type = 'Internal' THEN d.signature END) as internal
            FROM detections d
            JOIN signatures s ON d.signature = s.signature
            WHERE {whereClause}
            """;

        cmd.Parameters.AddWithValue("@startTime", startTime);
        cmd.Parameters.AddWithValue("@endTime", endTime);
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        if (!string.IsNullOrEmpty(country))
            cmd.Parameters.AddWithValue("@country", country);
        if (!string.IsNullOrEmpty(botType))
            cmd.Parameters.AddWithValue("@botType", botType);
        if (!string.IsNullOrEmpty(threat))
        {
            var threatRank = threat switch
            {
                "critical" or "high" => 3,
                "elevated" or "medium" => 2,
                "low" => 1,
                _ => 0
            };
            cmd.Parameters.AddWithValue("@threatRank", threatRank);
        }
        for (int i = 0; i < (domains?.Count ?? 0); i++)
            cmd.Parameters.AddWithValue($"@domain{i}", domains![i]);

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new FilterCounts
            {
                All = reader.GetInt32(0),
                Humans = reader.GetInt32(1),
                Bots = reader.GetInt32(2),
                Ai = reader.GetInt32(3),
                Search = reader.GetInt32(4),
                Tools = reader.GetInt32(5),
                Internal = reader.GetInt32(6)
            };
        }

        return new FilterCounts();
    }

    /// <summary>
    ///     Windowed variant of <see cref="GetTopBotsAsync"/>: aggregates directly from the
    ///     <c>detections</c> table so hit counts are bounded by the supplied time window.
    ///     Used when at least one of <paramref name="startTime"/> / <paramref name="endTime"/>
    ///     is set. The all-time (no-window) path continues to use the signatures table.
    /// </summary>
    private async Task<List<DashboardTopBotEntry>> GetTopBotsWindowedAsync(
        int count, DateTime? startTime, DateTime? endTime, string? audienceFilter = null,
        IReadOnlyList<string>? domains = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Internal-exclusion + bot/human gate, shared with Postgres. Top-bots defaults to
        // bots-only (mirrors the commercial store), so a null audience maps to "bots".
        // Fused rows always pass the WHERE; their split applies via the counter
        // expressions in the SELECT.
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);

        var timeWhere = new System.Text.StringBuilder();
        if (startTime.HasValue) timeWhere.Append(" AND timestamp >= @start");
        if (endTime.HasValue)   timeWhere.Append(" AND timestamp <= @end");

        await using var cmd = conn.CreateCommand();
        // Column order: signature(0), bot_name(1), bot_type(2), bot_probability(3), hit_count(4),
        //   last_seen(5), threat_score(6), action(7), threat_band(8), country_code(9), bytes_out(10), is_bot(11)
        // narrative/top_reasons are not stored per-detection row; they default to null for windowed results.
        // SQLite "bare column" rule (>= 3.7.11): when a query uses MAX() or MIN()
        // in the SELECT, every bare (un-aggregated) column comes from the SAME row
        // that produced the MAX/MIN. Picking MAX(timestamp) here means bot_name /
        // bot_type / bot_probability / is_bot / action / threat_band / country_code
        // all reflect the LATEST detection for the signature -- mirroring the
        // Postgres `(array_agg ORDER BY timestamp DESC))[1]` pattern. The previous
        // MAX(field) per column was alphabetical max for strings + "ever-bot" max
        // for the boolean, so a single past misclassification kept a re-classified
        // human stuck in the Bots filter forever at 0% bot probability.
        cmd.CommandText = $"""
            SELECT signature,
                   bot_name,
                   bot_type,
                   bot_probability,
                   SUM({FusedTotalExprFor(audienceFilter)})              AS hit_count,
                   MAX(timestamp)        AS last_seen,
                   AVG(threat_score)     AS threat_score,
                   action,
                   threat_band,
                   country_code,
                   SUM({FusedBytesExpr}) AS bytes_out,
                   is_bot,
                   user_agent_raw
            FROM detections
            WHERE 1=1{FusedAudienceWhere(audienceFilter)}{timeWhere}{domainPredicate}
            GROUP BY signature
            ORDER BY hit_count DESC
            LIMIT @count
            """;

        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end",   endTime.Value.ToString("O"));
        cmd.Parameters.AddWithValue("@count", count);
        foreach (var (name, value) in domainParams) cmd.Parameters.AddWithValue(name, value);

        var results = new List<DashboardTopBotEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DashboardTopBotEntry
            {
                PrimarySignature = reader.GetString(0),
                BotName          = reader.IsDBNull(1)  ? null : reader.GetString(1),
                BotType          = reader.IsDBNull(2)  ? null : reader.GetString(2),
                BotProbability   = reader.IsDBNull(3)  ? 0    : reader.GetDouble(3),
                HitCount         = reader.GetInt32(4),
                LastSeen         = DateTime.Parse(reader.GetString(5)),
                ThreatScore      = reader.IsDBNull(6)  ? 0    : reader.GetDouble(6),
                Action           = reader.IsDBNull(7)  ? null : reader.GetString(7),
                ThreatBand       = reader.IsDBNull(8)  ? null : reader.GetString(8),
                CountryCode      = reader.IsDBNull(9)  ? null : reader.GetString(9),
                BytesOut         = reader.IsDBNull(10) ? 0L   : Convert.ToInt64(reader.GetValue(10)),
                // Classification is derived from the same probability floor as
                // GetVisitorSegmentCountsAsync and never from the persisted
                // is_bot compatibility flag, which can be stale after a verdict
                // changes for a signature.
                IsKnownBot       = (reader.IsDBNull(3) ? 0 : reader.GetDouble(3)) >= _botFloor,
                UserAgent        = reader.IsDBNull(12) ? null : reader.GetString(12),
                // narrative and top_reasons_json live on the signatures table, not detections;
                // they are not available in the windowed path.
                Narrative        = null,
                TopReasons       = null,
                LastPath         = null,
            });
        }
        return results;
    }

    // SQLite lacks PERCENTILE_CONT, so p95 is approximated using avg + 90% of (max - avg).
    // Crude but consistent with the GetEndpointStatsAsync convention; the Postgres backend
    // returns true p95 via PERCENTILE_CONT (Task 10).
    // Column order (0-based): country_code(0), total(1), bots(2), avg_ms(3), max_ms(4), bytes_out(5)
    public async Task<IReadOnlyList<DashboardDomainStat>> GetDomainStatsAsync(
        DateTime? startTime = null, DateTime? endTime = null, int limit = 200, CancellationToken ct = default)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // ALL observed domains, one row each. Requests + Bots row-level EXCLUDE internal
        // self-traffic (bot_type='Internal') so they share /summary's bot universe
        // (AudiencePredicate excludes Internal rows) and the pool reconciles with the Traffic
        // counter at the same window — a mixed host (real traffic + gateway health/loopback rows)
        // would otherwise inflate the pool by orders of magnitude. is_internal is still classified
        // over ALL rows (a domain is internal iff it has zero non-Internal rows). No audience
        // filter and no domain predicate: the licensed-vs-pool split is the commercial overlay's
        // job. Mirrors the commercial Postgres GetDomainStatsAsync (COUNT(*) FILTER ... IS DISTINCT
        // FROM 'Internal').
        var where = new System.Text.StringBuilder("WHERE domain IS NOT NULL AND domain != ''");
        if (startTime.HasValue) where.Append(" AND timestamp >= @start");
        if (endTime.HasValue)   where.Append(" AND timestamp <= @end");

        await using var cmd = conn.CreateCommand();
        // Fused rows are grouped by (signature, hour, domain, country, bot_type),
        // so a fused row's bot_type is EXACTLY every absorbed row's bot_type —
        // the internal-exclusion CASE stays fully correct (Internal fused rows
        // are excluded like Internal raw rows).
        cmd.CommandText = $"""
            SELECT domain,
                   SUM(CASE WHEN fused = 1 AND bot_type IS NOT 'Internal' THEN hit_count
                            WHEN bot_type IS NOT 'Internal' THEN 1 ELSE 0 END) as requests,
                   SUM(CASE WHEN fused = 1 AND bot_type IS NOT 'Internal' THEN bot_count
                            WHEN bot_probability >= @botFloor AND bot_type IS NOT 'Internal' THEN 1 ELSE 0 END) as bots,
                   CASE WHEN SUM(CASE WHEN fused = 1 AND bot_type IS NOT 'Internal' THEN hit_count
                                      WHEN bot_type IS NOT 'Internal' THEN 1 ELSE 0 END) = 0
                        THEN 1 ELSE 0 END as is_internal,
                   SUM(CASE WHEN fused = 1 AND bot_type IS NOT 'Internal' THEN ms_sum
                            WHEN bot_type IS NOT 'Internal' THEN COALESCE(processing_time_ms, 0) ELSE 0 END) /
                     NULLIF(SUM(CASE WHEN fused = 1 AND bot_type IS NOT 'Internal' THEN hit_count
                                     WHEN bot_type IS NOT 'Internal' AND processing_time_ms IS NOT NULL THEN 1 ELSE 0 END), 0) AS avg_ms,
                   MAX(CASE WHEN fused = 1 AND bot_type IS NOT 'Internal' THEN ms_max
                            WHEN bot_type IS NOT 'Internal' THEN processing_time_ms ELSE NULL END) AS max_ms,
                   SUM(CASE WHEN fused = 1 AND bot_type IS NOT 'Internal' THEN bytes_sum
                            WHEN bot_type IS NOT 'Internal' THEN COALESCE(response_bytes, 0) ELSE 0 END) AS bytes_out
            FROM detections
            {where}
            GROUP BY domain
            ORDER BY requests DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        cmd.Parameters.AddWithValue("@limit", limit);
        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));

        var results = new List<DashboardDomainStat>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            results.Add(new DashboardDomainStat(
                Domain: reader.GetString(0),
                Requests: reader.IsDBNull(1) ? 0L : reader.GetInt64(1),
                Bots: reader.IsDBNull(2) ? 0L : reader.GetInt64(2),
                IsInternal: !reader.IsDBNull(3) && reader.GetInt64(3) == 1,
                AvgProcessingTimeMs: reader.IsDBNull(4) ? null : (double?)reader.GetDouble(4),
                MaxProcessingTimeMs: reader.IsDBNull(5) ? null : (double?)reader.GetDouble(5),
                BytesOut: reader.IsDBNull(6) ? 0L : reader.GetInt64(6)));
        }
        return results;
    }

    public async Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Build WHERE clause — mirrors Task-4 GetEndpointStatsAsync convention.
        var where = new System.Text.StringBuilder("WHERE country_code IS NOT NULL AND country_code != ''");
        if (startTime.HasValue) where.Append(" AND timestamp >= @start");
        if (endTime.HasValue)   where.Append(" AND timestamp <= @end");
        // Internal-exclusion + bot/human gate, shared with Postgres (see AudiencePredicate).
        // Fused rows always pass the WHERE; their split applies in the SELECT.
        where.Append(FusedAudienceWhere(audienceFilter));
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);
        if (domainPredicate.Length > 0) where.Append(domainPredicate);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT country_code,
                   SUM({FusedTotalExprFor(audienceFilter)}) as total,
                   SUM({FusedBotsExpr(audienceFilter)}) as bots,
                   SUM({FusedMsSumExpr}) / NULLIF(SUM({FusedTotalExprFor(audienceFilter)}), 0) AS avg_ms,
                   MAX({FusedMsMaxExpr}) AS max_ms,
                   SUM({FusedBytesExpr}) as bytes_out
            FROM detections
            {where}
            GROUP BY country_code
            ORDER BY total DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);
        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));
        foreach (var (name, value) in domainParams) cmd.Parameters.AddWithValue(name, value);

        var results = new List<DashboardCountryStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(1);
            var bots  = reader.GetInt32(2);
            var avgMs = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
            var maxMs = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);
            // p95 approximation: avg + 90% of the gap to max. Matches the Postgres
            // backend convention; real percentile requires PERCENTILE_CONT (Task 10).
            var p95Ms = avgMs + (maxMs - avgMs) * 0.9;
            results.Add(new DashboardCountryStats
            {
                CountryCode         = reader.GetString(0),
                TotalCount          = total,
                BotCount            = bots,
                BotRate             = total > 0 ? (double)bots / total : 0,
                AvgProcessingTimeMs = Math.Round(avgMs, 2),
                MaxProcessingTimeMs = Math.Round(maxMs, 2),
                P95ProcessingTimeMs = Math.Round(p95Ms, 2),
                BytesOut            = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5)),
            });
        }
        return results;
    }

    public async Task<DashboardCountryDetail?> GetCountryDetailAsync(string countryCode, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var timeFilter = "";
        if (startTime.HasValue) timeFilter += " AND timestamp >= @start";
        if (endTime.HasValue)   timeFilter += " AND timestamp <= @end";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                SUM({FusedTotalExpr}) AS total,
                SUM({FusedBotsExpr(null)}) AS bots
            FROM detections
            WHERE country_code = @cc{timeFilter}
            """;
        cmd.Parameters.AddWithValue("@cc", countryCode);
        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(0)) return null;

        var total = reader.GetInt32(0);
        if (total == 0) return null;
        var bots = reader.GetInt32(1);

        return new DashboardCountryDetail
        {
            CountryCode = countryCode,
            TotalCount = total,
            BotCount = bots,
            BotRate = total > 0 ? (double)bots / total : 0,
            TopBotTypes = new Dictionary<string, int>(),
            TopBots = new List<DashboardTopBotEntry>()
        };
    }

    public async Task UpsertEndpointStatsAsync(
        string method,
        string path,
        string? domain,
        bool isBot,
        double botProbability,
        double? threatScore,
        long? responseBytes,
        double? processingTimeMs,
        int statusCode,
        int? upstreamStatusCode,
        DateTime timestamp,
        string? cacheStatus = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Cache-status tally path for SQLite's json_insert/json_extract: the status
        // string is the JSON object key, so '$."HIT"' / '$."MISS"' etc. (a null
        // cache status means no cache layer answered — the tally stays untouched).
        var tallyPath = cacheStatus is null ? null : "$.\"" + cacheStatus.Replace("\"", "\\\"") + "\"";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO endpoint_stats
                (method, path, domain, is_bot, hit_count,
                 bytes_sum, ms_sum, ms_min, ms_max, threat_sum, last_seen,
                 s2xx, s3xx, s4xx, s5xx,
                 us2xx, us3xx, us4xx, us5xx, us_none,
                 cache_status_tally)
            VALUES
                (@method, @path, @domain, @isBot, 1,
                 @bytes, @ms, @ms, @ms, @threat, @lastSeen,
                 @s2xx, @s3xx, @s4xx, @s5xx,
                 @us2xx, @us3xx, @us4xx, @us5xx, @usNone,
                 @initialTally)
            ON CONFLICT(method, path, domain, is_bot) DO UPDATE SET
                hit_count = hit_count + 1,
                bytes_sum = bytes_sum + @bytes,
                ms_sum    = ms_sum + @ms,
                ms_min    = CASE WHEN ms_min = 0 THEN @ms WHEN @ms < ms_min THEN @ms ELSE ms_min END,
                ms_max    = MAX(ms_max, @ms),
                threat_sum = threat_sum + @threat,
                last_seen = MAX(last_seen, @lastSeen),
                s2xx      = s2xx  + @s2xx,
                s3xx      = s3xx  + @s3xx,
                s4xx      = s4xx  + @s4xx,
                s5xx      = s5xx  + @s5xx,
                us2xx     = us2xx + @us2xx,
                us3xx     = us3xx + @us3xx,
                us4xx     = us4xx + @us4xx,
                us5xx     = us5xx + @us5xx,
                us_none   = us_none + @usNone,
                cache_status_tally = CASE WHEN @tallyPath IS NULL THEN cache_status_tally
                    ELSE json_insert(cache_status_tally, @tallyPath,
                        COALESCE(json_extract(cache_status_tally, @tallyPath), 0) + 1) END
            """;
        var dom = domain ?? string.Empty;
        cmd.Parameters.AddWithValue("@method", method);
        cmd.Parameters.AddWithValue("@path", path);
        cmd.Parameters.AddWithValue("@domain", dom);
        cmd.Parameters.AddWithValue("@isBot", isBot ? 1 : 0);
        cmd.Parameters.AddWithValue("@bytes", responseBytes ?? 0L);
        var ms = processingTimeMs ?? 0;
        cmd.Parameters.AddWithValue("@ms", ms);
        cmd.Parameters.AddWithValue("@threat", threatScore ?? 0);
        cmd.Parameters.AddWithValue("@lastSeen", timestamp.ToString("O"));
        cmd.Parameters.AddWithValue("@s2xx", statusCode is >= 200 and <= 299 ? 1 : 0);
        cmd.Parameters.AddWithValue("@s3xx", statusCode is >= 300 and <= 399 ? 1 : 0);
        cmd.Parameters.AddWithValue("@s4xx", statusCode is >= 400 and <= 499 ? 1 : 0);
        cmd.Parameters.AddWithValue("@s5xx", statusCode is >= 500 and <= 599 ? 1 : 0);
        var us = upstreamStatusCode;
        cmd.Parameters.AddWithValue("@us2xx", us is >= 200 and <= 299 ? 1 : 0);
        cmd.Parameters.AddWithValue("@us3xx", us is >= 300 and <= 399 ? 1 : 0);
        cmd.Parameters.AddWithValue("@us4xx", us is >= 400 and <= 499 ? 1 : 0);
        cmd.Parameters.AddWithValue("@us5xx", us is >= 500 and <= 599 ? 1 : 0);
        cmd.Parameters.AddWithValue("@usNone", us is null ? 1 : 0);
        // Initial tally for a brand-new row: the single status, or the empty object
        // when no cache layer answered. The DO UPDATE branch increments instead.
        cmd.Parameters.AddWithValue("@initialTally",
            tallyPath is null ? "{}" : "{\"" + cacheStatus!.Replace("\"", "\\\"") + "\": 1}");
        cmd.Parameters.AddWithValue("@tallyPath", (object?)tallyPath ?? DBNull.Value);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(
        int count = 50,
        DateTime? startTime = null,
        DateTime? endTime = null,
        string? audienceFilter = null,
        IReadOnlyList<string>? domains = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Build WHERE clause. Time predicates fix the latent bug where startTime/endTime were
        // accepted by the signature but never applied. The audience filter maps "humans"/"bots"
        // Read from the bounded endpoint_stats table — split by is_bot so
        // audience filters work correctly. Aggregate across is_bot for the
        // default (all) view.
        var honeyPotOnly = string.Equals(audienceFilter, "honeypot", StringComparison.OrdinalIgnoreCase);
        await using var cmd = conn.CreateCommand();
        // Check if the bounded table has any data; fall back to detections
        // scan on a pre-migration (empty) host. Also fall back for audience
        // filters that include Internal traffic (endpoint_stats excludes
        // Internal at upsert time to keep the operator-facing list clean).
        await using (var checkCmd = conn.CreateCommand())
        {
            var fallbackAudiences = string.Equals(audienceFilter, "all_incl_internal", StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(audienceFilter, "internal", StringComparison.OrdinalIgnoreCase);
            checkCmd.CommandText = "SELECT COUNT(*) FROM endpoint_stats";
            var rowCount = (long)(await checkCmd.ExecuteScalarAsync())!;
            if (rowCount == 0 || fallbackAudiences)
                return await GetEndpointStatsFromDetectionsFallbackAsync(
                    conn, count, startTime, endTime, audienceFilter, domains);
        }

        // Build WHERE for the inner per-is_bot row filter, then GROUP BY
        // (method, path, domain) to merge bot + human rows.
        var innerWhere = new System.Text.StringBuilder("WHERE 1=1");
        if (startTime.HasValue) innerWhere.Append(" AND last_seen >= @start");
        if (endTime.HasValue)   innerWhere.Append(" AND last_seen <= @end");
        // Audience: filter to is_bot = 1 (bots) or 0 (humans), or all rows.
        if (string.Equals(audienceFilter, "bots", StringComparison.OrdinalIgnoreCase))
            innerWhere.Append(" AND is_bot = 1");
        else if (string.Equals(audienceFilter, "humans", StringComparison.OrdinalIgnoreCase))
            innerWhere.Append(" AND is_bot = 0");
        // "honeypot" is a post-query path-based filter — no is_bot gate.
        if (domains is { Count: > 0 })
        {
            innerWhere.Append(" AND domain IN (");
            for (var i = 0; i < domains.Count; i++)
            {
                if (i > 0) innerWhere.Append(',');
                innerWhere.Append($"@dom{i}");
                cmd.Parameters.AddWithValue($"@dom{i}", domains[i]);
            }
            innerWhere.Append(')');
        }

        // Aggregate across the is_bot split. For the default audience, SUM
        // both bot and human rows; for audience-filtered queries the inner
        // WHERE already restricts to one is_bot value so the SUM is exact.
        cmd.CommandText = $"""
            SELECT method, path, domain,
                   SUM(hit_count) as total,
                   SUM(CASE WHEN is_bot = 1 THEN hit_count ELSE 0 END) as bots,
                   SUM(bytes_sum) as bytes_out,
                   SUM(ms_sum) as ms_sum,
                   MAX(ms_max) as ms_max,
                   SUM(threat_sum) as threat_sum,
                   MAX(last_seen) as last_seen,
                   SUM(s2xx) as s2xx, SUM(s3xx) as s3xx,
                   SUM(s4xx) as s4xx, SUM(s5xx) as s5xx,
                   SUM(us2xx) as us2xx, SUM(us3xx) as us3xx,
                   SUM(us4xx) as us4xx, SUM(us5xx) as us5xx,
                   SUM(us_none) as us_none
            FROM endpoint_stats
            {innerWhere}
            GROUP BY method, path, domain
            ORDER BY total DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);
        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));

        var results = new List<DashboardEndpointStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(3);   // SUM(hit_count)
            var bots  = reader.GetInt32(4);   // SUM(bot hit_count)
            var sumMs = reader.GetDouble(6);  // SUM(ms_sum)
            var maxMs = reader.IsDBNull(7) ? 0 : reader.GetDouble(7);
            var avgMs = total > 0 ? sumMs / total : 0;
            var p95Ms = avgMs + (maxMs - avgMs) * 0.9;
            var path = reader.GetString(1);
            var honeypotTier = Mostlylucid.BotDetection.Honeypot.HoneypotPathDefinitions
                .Classify(path, out _);
            var isHoneypot = honeypotTier > Mostlylucid.BotDetection.Honeypot.HoneypotTier.None;
            if (honeyPotOnly && !isHoneypot) continue;
            var threatSum = reader.IsDBNull(8) ? 0 : reader.GetDouble(8);
            results.Add(new DashboardEndpointStats
            {
                Method              = reader.GetString(0),
                Path                = path,
                TotalCount          = total,
                BotCount            = bots,
                BotRate             = total > 0 ? (double)bots / total : 0,
                UniqueSignatures    = 0, // bounded table
                AvgProcessingTimeMs = avgMs,
                MinProcessingTimeMs = 0,
                MaxProcessingTimeMs = maxMs,
                P95ProcessingTimeMs = p95Ms,
                AvgThreatScore      = total > 0 ? threatSum / total : 0,
                LastSeen            = DateTime.Parse(reader.GetString(9)),
                BytesOut            = reader.IsDBNull(5) ? 0L : Convert.ToInt64(reader.GetValue(5)),
                Status2xx           = reader.IsDBNull(10) ? 0 : reader.GetInt32(10),
                Status3xx           = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                Status4xx           = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                Status5xx           = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                UpstreamStatus2xx   = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                UpstreamStatus3xx   = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
                UpstreamStatus4xx   = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                UpstreamStatus5xx   = reader.IsDBNull(17) ? 0 : reader.GetInt32(17),
                UpstreamNoneCount   = reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                IsHoneypot          = isHoneypot,
            });
        }
        return results;
    }

    /// <summary>
    ///     Fallback: scan the per-request detections table for endpoint stats
    ///     when the bounded <c>endpoint_stats</c> table is empty (pre-migration
    ///     hosts where the upsert hasn't been running yet).
    /// </summary>
    private async Task<List<DashboardEndpointStats>> GetEndpointStatsFromDetectionsFallbackAsync(
        SqliteConnection conn,
        int count,
        DateTime? startTime,
        DateTime? endTime,
        string? audienceFilter,
        IReadOnlyList<string>? domains)
    {
        var honeyPotOnly = string.Equals(audienceFilter, "honeypot", StringComparison.OrdinalIgnoreCase);
        var where = new System.Text.StringBuilder("WHERE fused = 0");
        if (startTime.HasValue) where.Append(" AND timestamp >= @start");
        if (endTime.HasValue)   where.Append(" AND timestamp <= @end");
        where.Append(AudiencePredicate(audienceFilter));
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);
        if (domainPredicate.Length > 0) where.Append(domainPredicate);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT method, path,
                   COUNT(*) as total,
                   SUM(CASE WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END) as bots,
                   COUNT(DISTINCT signature) as sigs,
                   AVG(processing_time_ms) as avg_ms,
                   MIN(processing_time_ms) as min_ms,
                   MAX(processing_time_ms) as max_ms,
                   AVG(threat_score) as avg_threat,
                   MAX(timestamp) as last_seen,
                   COALESCE(SUM(response_bytes), 0) as bytes_out,
                   SUM(CASE WHEN status_code BETWEEN 200 AND 299 THEN 1 ELSE 0 END) as s2xx,
                   SUM(CASE WHEN status_code BETWEEN 300 AND 399 THEN 1 ELSE 0 END) as s3xx,
                   SUM(CASE WHEN status_code BETWEEN 400 AND 499 THEN 1 ELSE 0 END) as s4xx,
                   SUM(CASE WHEN status_code BETWEEN 500 AND 599 THEN 1 ELSE 0 END) as s5xx,
                   SUM(CASE WHEN upstream_status_code BETWEEN 200 AND 299 THEN 1 ELSE 0 END) as us2xx,
                   SUM(CASE WHEN upstream_status_code BETWEEN 300 AND 399 THEN 1 ELSE 0 END) as us3xx,
                   SUM(CASE WHEN upstream_status_code BETWEEN 400 AND 499 THEN 1 ELSE 0 END) as us4xx,
                   SUM(CASE WHEN upstream_status_code BETWEEN 500 AND 599 THEN 1 ELSE 0 END) as us5xx,
                   SUM(CASE WHEN upstream_status_code IS NULL THEN 1 ELSE 0 END) as us_none
            FROM detections
            {where}
            GROUP BY method, path
            ORDER BY total DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);
        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));
        foreach (var (n, v) in domainParams) cmd.Parameters.AddWithValue(n, v);

        var results = new List<DashboardEndpointStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(2);
            var bots  = reader.GetInt32(3);
            var avgMs = reader.GetDouble(5);
            var minMs = reader.IsDBNull(6) ? 0 : reader.GetDouble(6);
            var maxMs = reader.IsDBNull(7) ? 0 : reader.GetDouble(7);
            var p95Ms = avgMs + (maxMs - avgMs) * 0.9;
            var path = reader.GetString(1);
            var honeypotTier = Mostlylucid.BotDetection.Honeypot.HoneypotPathDefinitions
                .Classify(path, out _);
            var isHoneypot = honeypotTier > Mostlylucid.BotDetection.Honeypot.HoneypotTier.None;
            if (honeyPotOnly && !isHoneypot) continue;
            results.Add(new DashboardEndpointStats
            {
                Method              = reader.GetString(0),
                Path                = path,
                TotalCount          = total,
                BotCount            = bots,
                BotRate             = total > 0 ? (double)bots / total : 0,
                UniqueSignatures    = reader.GetInt32(4),
                AvgProcessingTimeMs = avgMs,
                MinProcessingTimeMs = minMs,
                MaxProcessingTimeMs = maxMs,
                P95ProcessingTimeMs = p95Ms,
                AvgThreatScore      = reader.IsDBNull(8) ? 0 : reader.GetDouble(8),
                LastSeen            = DateTime.Parse(reader.GetString(9)),
                BytesOut            = reader.IsDBNull(10) ? 0L : Convert.ToInt64(reader.GetValue(10)),
                Status2xx           = reader.IsDBNull(11) ? 0 : reader.GetInt32(11),
                Status3xx           = reader.IsDBNull(12) ? 0 : reader.GetInt32(12),
                Status4xx           = reader.IsDBNull(13) ? 0 : reader.GetInt32(13),
                Status5xx           = reader.IsDBNull(14) ? 0 : reader.GetInt32(14),
                UpstreamStatus2xx   = reader.IsDBNull(15) ? 0 : reader.GetInt32(15),
                UpstreamStatus3xx   = reader.IsDBNull(16) ? 0 : reader.GetInt32(16),
                UpstreamStatus4xx   = reader.IsDBNull(17) ? 0 : reader.GetInt32(17),
                UpstreamStatus5xx   = reader.IsDBNull(18) ? 0 : reader.GetInt32(18),
                UpstreamNoneCount   = reader.IsDBNull(19) ? 0 : reader.GetInt32(19),
                IsHoneypot          = isHoneypot,
            });
        }
        return results;
    }


    public async Task<List<SignatureEndpointStats>> GetEndpointStatsForSignatureAsync(
        string signature,
        int topN = 25,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(signature)) return new List<SignatureEndpointStats>();
        if (topN <= 0) topN = 25;
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        // Aggregate every persisted detection for this signature by (method, path).
        // DominantAction / DominantRiskBand come from a per-row MAX(COUNT) over the
        // grouped slice — SQLite doesn't have MODE() so we approximate with the
        // most-frequent label via a sub-aggregate per row. For a typical visitor
        // hitting <= 25 distinct endpoints this is cheap; the LIMIT caps worst-case.
        //
        // P95 uses the same avg + 0.9 * (max - avg) approximation as the existing
        // p95 columns elsewhere in this file. PostgreSQL gets the real percentile.
        //
        // Status mix is summed from status_code; 0 (not yet captured on legacy rows)
        // falls through all three buckets, which is fine — the operator sees zeros
        // and can ignore the column.
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                method,
                path,
                COUNT(*)                                                                    AS hits,
                AVG(processing_time_ms)                                                     AS avg_ms,
                MAX(processing_time_ms)                                                     AS max_ms,
                SUM(CASE WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END) * 1.0 / COUNT(*)                AS bot_rate,
                AVG(COALESCE(threat_score, 0))                                              AS avg_threat,
                MAX(timestamp)                                                              AS last_seen,
                SUM(CASE WHEN status_code BETWEEN 200 AND 299 THEN 1 ELSE 0 END)            AS s2xx,
                SUM(CASE WHEN status_code BETWEEN 400 AND 499 THEN 1 ELSE 0 END)            AS s4xx,
                SUM(CASE WHEN status_code BETWEEN 500 AND 599 THEN 1 ELSE 0 END)            AS s5xx,
                (SELECT risk_band FROM detections d2
                  WHERE d2.signature = detections.signature
                    AND d2.method = detections.method
                    AND d2.path = detections.path
                    AND risk_band IS NOT NULL
                  GROUP BY risk_band
                  ORDER BY COUNT(*) DESC, risk_band ASC
                  LIMIT 1)                                                                  AS dominant_risk,
                (SELECT action FROM detections d3
                  WHERE d3.signature = detections.signature
                    AND d3.method = detections.method
                    AND d3.path = detections.path
                    AND action IS NOT NULL
                  GROUP BY action
                  ORDER BY COUNT(*) DESC, action ASC
                  LIMIT 1)                                                                  AS dominant_action
            FROM detections
            WHERE signature = @sig
              -- Drill-down: fused rows lost their (method, path) shape.
              AND fused = 0
            GROUP BY method, path
            ORDER BY hits DESC
            LIMIT @top
            """;
        cmd.Parameters.AddWithValue("@sig", signature);
        cmd.Parameters.AddWithValue("@top", topN);

        var rows = new List<SignatureEndpointStats>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var avgMs = reader.IsDBNull(3) ? 0.0 : reader.GetDouble(3);
            var maxMs = reader.IsDBNull(4) ? 0.0 : reader.GetDouble(4);
            var p95   = avgMs + (maxMs - avgMs) * 0.9;
            rows.Add(new SignatureEndpointStats
            {
                Method              = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Path                = reader.IsDBNull(1) ? "" : reader.GetString(1),
                HitCount            = reader.GetInt32(2),
                AvgProcessingTimeMs = avgMs,
                MaxProcessingTimeMs = maxMs,
                P95ProcessingTimeMs = p95,
                BotRate             = reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                AvgThreatScore      = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                LastSeen            = reader.IsDBNull(7) ? default : DateTime.Parse(reader.GetString(7)),
                Status2xx           = reader.GetInt32(8),
                Status4xx           = reader.GetInt32(9),
                Status5xx           = reader.GetInt32(10),
                DominantRiskBand    = reader.IsDBNull(11) ? null : reader.GetString(11),
                DominantAction      = reader.IsDBNull(12) ? null : reader.GetString(12),
            });
        }
        return rows;
    }

    public async Task<DashboardEndpointDetail?> GetEndpointDetailAsync(string method, string path, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var timeFilter = "";
        if (startTime.HasValue) timeFilter += " AND timestamp >= @start";
        if (endTime.HasValue)   timeFilter += " AND timestamp <= @end";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                COUNT(*) AS total,
                SUM(CASE WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END) AS bots,
                COUNT(DISTINCT signature) AS sigs,
                AVG(processing_time_ms) AS avg_ms,
                AVG(threat_score) AS avg_threat
            FROM detections
            WHERE method = @method AND path = @path{timeFilter}
            """;
        cmd.Parameters.AddWithValue("@method", method);
        cmd.Parameters.AddWithValue("@path", path);
        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        if (endTime.HasValue)   cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.IsDBNull(0)) return null;

        var total = reader.GetInt32(0);
        if (total == 0) return null;
        var bots = reader.GetInt32(1);

        var uniqueSigs = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
        var avgMs = reader.IsDBNull(3) ? 0 : reader.GetDouble(3);
        var avgThreat = reader.IsDBNull(4) ? 0 : reader.GetDouble(4);
        await reader.CloseAsync();

        // Query behavioral profiles grouped by bot/human
        await using var profileCmd = conn.CreateCommand();
        profileCmd.CommandText = $"""
            SELECT
                is_bot,
                COUNT(*) AS cnt,
                AVG(COALESCE(bot_probability, 0)) AS avg_prob,
                AVG(COALESCE(confidence, 0)) AS avg_conf,
                AVG(COALESCE(threat_score, 0)) AS avg_threat,
                AVG(COALESCE(processing_time_ms, 0)) AS avg_ms,
                SUM(CASE WHEN action = 'block' THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS block_rate,
                SUM(CASE WHEN status_code >= 400 THEN 1 ELSE 0 END) * 100.0 / COUNT(*) AS error_rate
            FROM detections
            WHERE method = @method AND path = @path{timeFilter}
            GROUP BY is_bot
            """;
        profileCmd.Parameters.AddWithValue("@method", method);
        profileCmd.Parameters.AddWithValue("@path", path);
        if (startTime.HasValue) profileCmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        if (endTime.HasValue)   profileCmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));

        EndpointBehavioralProfile? botProfile = null, humanProfile = null;
        double maxMs = Math.Max(avgMs, 1);

        await using var profileReader = await profileCmd.ExecuteReaderAsync();
        while (await profileReader.ReadAsync())
        {
            var isBot = profileReader.GetInt32(0) == 1;
            var cnt = profileReader.GetInt32(1);
            var profile = new EndpointBehavioralProfile
            {
                SampleCount    = cnt,
                Probability    = profileReader.GetDouble(2) * 100,
                Confidence     = profileReader.GetDouble(3) * 100,
                ThreatScore    = Math.Min(profileReader.GetDouble(4) * 10, 100),
                LatencyScore   = Math.Min(profileReader.GetDouble(5) / maxMs * 100, 100),
                BlockRate      = profileReader.GetDouble(6),
                ErrorRate      = profileReader.GetDouble(7)
            };
            if (isBot) botProfile = profile;
            else humanProfile = profile;
        }

        // Overall profile
        EndpointBehavioralProfile? overallProfile = null;
        if (botProfile != null || humanProfile != null)
        {
            var totalN = (botProfile?.SampleCount ?? 0) + (humanProfile?.SampleCount ?? 0);
            if (totalN > 0)
            {
                double W(EndpointBehavioralProfile? p, Func<EndpointBehavioralProfile, double> f)
                    => p == null ? 0 : f(p) * p.SampleCount / totalN;
                overallProfile = new EndpointBehavioralProfile
                {
                    SampleCount  = totalN,
                    Probability  = W(botProfile, p => p.Probability) + W(humanProfile, p => p.Probability),
                    Confidence   = W(botProfile, p => p.Confidence)  + W(humanProfile, p => p.Confidence),
                    ThreatScore  = W(botProfile, p => p.ThreatScore) + W(humanProfile, p => p.ThreatScore),
                    LatencyScore = W(botProfile, p => p.LatencyScore)+ W(humanProfile, p => p.LatencyScore),
                    BlockRate    = W(botProfile, p => p.BlockRate)   + W(humanProfile, p => p.BlockRate),
                    ErrorRate    = W(botProfile, p => p.ErrorRate)   + W(humanProfile, p => p.ErrorRate)
                };
            }
        }

        return new DashboardEndpointDetail
        {
            Method = method,
            Path = path,
            TotalCount = total,
            BotCount = bots,
            BotRate = total > 0 ? (double)bots / total : 0,
            UniqueSignatures = uniqueSigs,
            AvgProcessingTimeMs = avgMs,
            AvgThreatScore = avgThreat,
            TopActions = new Dictionary<string, int>(),
            TopCountries = new Dictionary<string, int>(),
            RiskBands = new Dictionary<string, int>(),
            TopBots = new List<DashboardTopBotEntry>(),
            RecentDetections = new List<SignatureDetectionRow>(),
            BotProfile = botProfile,
            HumanProfile = humanProfile,
            OverallProfile = overallProfile
        };
    }

    public async Task<List<HoneypotHitRow>> GetHoneypotHitsAsync(
        int count = 50, DateTime? startTime = null, DateTime? endTime = null,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        var sql = """
            SELECT path,
                   COUNT(*)                       AS hit_count,
                   COUNT(DISTINCT signature)      AS distinct_sigs,
                   MIN(timestamp)                 AS first_seen,
                   MAX(timestamp)                 AS last_seen,
                   MAX(bot_name)                  AS sample_bot_name
            FROM detections
            WHERE action IN ('honeypot-response', 'simulation-pack')
            """;

        if (startTime.HasValue)
        {
            sql += " AND timestamp >= @start";
            cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        }
        if (endTime.HasValue)
        {
            sql += " AND timestamp <= @end";
            cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));
        }

        sql += " GROUP BY path ORDER BY hit_count DESC LIMIT @count";
        cmd.Parameters.AddWithValue("@count", count);
        cmd.CommandText = sql;

        var results = new List<HoneypotHitRow>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        while (await reader.ReadAsync(ct))
        {
            var rawPath = reader.IsDBNull(0) ? "/" : reader.GetString(0);
            // Re-classify against the catalog so the UI shows tier badges
            // even when the action column doesn't carry the tier directly.
            var normalized = Mostlylucid.BotDetection.Honeypot.HoneypotPathDefinitions.NormalizePath(rawPath);
            var classification = Mostlylucid.BotDetection.Honeypot.HoneypotPathDefinitions.ClassifyDetailed(normalized);
            if (classification.Tier == Mostlylucid.BotDetection.Honeypot.HoneypotTier.None) continue; // skip rows that aren't actually honeypot

            var distinctSigs = reader.GetInt32(2);
            results.Add(new HoneypotHitRow
            {
                Path = rawPath,
                Tier = (int)classification.Tier,
                Category = classification.Category,
                MatchedPattern = classification.Pattern,
                HitCount = reader.GetInt32(1),
                DistinctSignatures = distinctSigs,
                FirstSeen = DateTime.Parse(reader.GetString(3), null, System.Globalization.DateTimeStyles.RoundtripKind),
                LastSeen = DateTime.Parse(reader.GetString(4), null, System.Globalization.DateTimeStyles.RoundtripKind),
                SampleBotName = reader.IsDBNull(5) ? null : reader.GetString(5),
                Why = BuildWhyChips(classification.Tier, classification.Category, classification.Pattern, normalized, distinctSigs)
            });
        }

        return results;
    }

    /// <summary>
    ///     Deterministic explanation chips derived from tier + category + the
    ///     matched pattern. Intentionally short and operator-facing -- the
    ///     chip text is what gets shown in the "Why" column on the Honeypot
    ///     tab. The intent chip comes straight from <see cref="LabelForCategory"/>
    ///     so the dashboard always agrees with the catalog -- no parallel
    ///     string matching.
    /// </summary>
    private static IReadOnlyList<string> BuildWhyChips(
        Mostlylucid.BotDetection.Honeypot.HoneypotTier tier,
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory category,
        string? pattern,
        string normalizedPath,
        int distinctVisitors)
    {
        var chips = new List<string>(4);

        chips.Add(LabelForCategory(category, tier));

        // Pattern chip (collapsed when same as path).
        if (!string.IsNullOrEmpty(pattern) &&
            !string.Equals(pattern, normalizedPath, StringComparison.OrdinalIgnoreCase))
        {
            chips.Add($"matches {pattern}");
        }

        if (distinctVisitors >= 5)
            chips.Add($"{distinctVisitors} distinct visitors");

        return chips;
    }

    /// <summary>
    ///     Short, operator-facing label for a honeypot category. Stable text
    ///     so screenshots and runbooks survive across versions; if a new
    ///     category is added to <see cref="Mostlylucid.BotDetection.Honeypot.HoneypotCategory"/>
    ///     without a label here it falls back to the tier-derived default.
    /// </summary>
    public static string LabelForCategory(
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory category,
        Mostlylucid.BotDetection.Honeypot.HoneypotTier tier) => category switch
    {
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Credentials   => "credentials theft",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Config        => "config file leak",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.VersionControl => "version-control exposure",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Database      => "database admin probe",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Webshell      => "webshell upload",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Admin         => "admin-panel probe",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Debug         => "debug-endpoint probe",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Backup        => "database/backup dump",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Metadata      => "metadata SSRF probe",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.PathTraversal => "path-traversal probe",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.BuildArtifact => "build-artifact probe",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.ApiDoc        => "api-doc enumeration",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Cgi           => "CGI probe",
        Mostlylucid.BotDetection.Honeypot.HoneypotCategory.Cms           => "CMS probe",
        _ => tier == Mostlylucid.BotDetection.Honeypot.HoneypotTier.Always
            ? "always-honeypot"
            : "probable scanner",
    };

    public async Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, IReadOnlyList<string>? domains = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        var sql = """
            SELECT timestamp, signature, path, bot_name, bot_type, bot_probability,
                   threat_score, threat_band, country_code, action, status_code, user_agent_raw
            FROM detections
            WHERE (action = 'simulation-pack'
                   OR threat_band IN ('Critical', 'High'))
              -- Drill-down feed: fused summary rows are not real events
              -- (their representative threat band can match even though the
              -- fusion gate kept their threat score below the ceiling).
              AND fused = 0
            """;

        if (startTime.HasValue)
        {
            sql += " AND timestamp >= @start";
            cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
        }
        if (endTime.HasValue)
        {
            sql += " AND timestamp <= @end";
            cmd.Parameters.AddWithValue("@end", endTime.Value.ToString("O"));
        }

        if (domains is { Count: > 0 })
            sql += " AND " + BuildDomainPredicate(domains, "d");

        sql += " ORDER BY timestamp DESC LIMIT @count";
        cmd.Parameters.AddWithValue("@count", count);
        cmd.CommandText = sql;

        var results = new List<ThreatEntry>();
        await using var reader = await cmd.ExecuteReaderAsync();

        var tOrdTimestamp   = reader.GetOrdinal("timestamp");
        var tOrdSignature   = reader.GetOrdinal("signature");
        var tOrdPath        = reader.GetOrdinal("path");
        var tOrdBotName     = reader.GetOrdinal("bot_name");
        var tOrdBotType     = reader.GetOrdinal("bot_type");
        var tOrdProb        = reader.GetOrdinal("bot_probability");
        var tOrdThreat      = reader.GetOrdinal("threat_score");
        var tOrdThreatBand  = reader.GetOrdinal("threat_band");
        var tOrdCountry     = reader.GetOrdinal("country_code");
        var tOrdAction      = reader.GetOrdinal("action");
        var tOrdUserAgent   = reader.GetOrdinal("user_agent_raw");

        while (await reader.ReadAsync())
        {
            var path = reader.IsDBNull(tOrdPath) ? "/" : reader.GetString(tOrdPath);
            var action = reader.IsDBNull(tOrdAction) ? null : reader.GetString(tOrdAction);
            var threatScore = reader.IsDBNull(tOrdThreat) ? 0 : reader.GetDouble(tOrdThreat);

            // Infer CVE and pack info from path patterns
            string? cveId = null;
            string? cveSeverity = null;
            string? packId = null;

            if (path.StartsWith("/wp-", StringComparison.OrdinalIgnoreCase))
            {
                packId = "wordpress-5.9";
                cveSeverity = threatScore >= 0.8 ? "critical" : threatScore >= 0.55 ? "high" : "medium";
            }
            else if (path.StartsWith("/.env", StringComparison.OrdinalIgnoreCase))
            {
                cveSeverity = "high";
            }
            else if (path.StartsWith("/.git", StringComparison.OrdinalIgnoreCase))
            {
                cveSeverity = "high";
            }
            else if (threatScore >= 0.8)
            {
                cveSeverity = "critical";
            }
            else if (threatScore >= 0.55)
            {
                cveSeverity = "high";
            }
            else if (threatScore >= 0.35)
            {
                cveSeverity = "medium";
            }

            var inHoneypot = action != null && action.Contains("simulation-pack", StringComparison.OrdinalIgnoreCase);

            results.Add(new ThreatEntry
            {
                Timestamp      = DateTime.Parse(reader.GetString(tOrdTimestamp)),
                Signature      = reader.GetString(tOrdSignature),
                Path           = path,
                BotName        = reader.IsDBNull(tOrdBotName)    ? null : reader.GetString(tOrdBotName),
                BotType        = reader.IsDBNull(tOrdBotType)    ? null : reader.GetString(tOrdBotType),
                BotProbability = reader.GetDouble(tOrdProb),
                ThreatScore    = threatScore,
                ThreatBand     = reader.IsDBNull(tOrdThreatBand) ? null : reader.GetString(tOrdThreatBand),
                CountryCode    = reader.IsDBNull(tOrdCountry)    ? null : reader.GetString(tOrdCountry),
                CveId          = cveId,
                CveSeverity    = cveSeverity,
                PackId         = packId,
                InHoneypot     = inHoneypot,
                UserAgent      = reader.IsDBNull(tOrdUserAgent) ? null : reader.GetString(tOrdUserAgent),
            });
        }

        return results;
    }

    // Ordinal cache for ReadSignature — resolved lazily on first call, valid for the lifetime
    // of a single SqliteDataReader (same schema). Stored as statics because the columns are fixed.
    private static int[]? _sigOrdinals;

    private static DashboardSignatureEvent ReadSignature(SqliteDataReader reader)
    {
        // Resolve ordinals once per reader schema (all signatures rows have the same columns)
        _sigOrdinals ??=
        [
            reader.GetOrdinal("signature"),
            reader.GetOrdinal("last_seen"),
            reader.GetOrdinal("risk_band"),
            reader.GetOrdinal("bot_name"),
            reader.GetOrdinal("bot_type"),
            reader.GetOrdinal("is_bot"),
            reader.GetOrdinal("bot_probability"),
            reader.GetOrdinal("confidence"),
            reader.GetOrdinal("action"),
            reader.GetOrdinal("hit_count"),
            reader.GetOrdinal("processing_time_ms"),
            reader.GetOrdinal("threat_score"),
            reader.GetOrdinal("threat_band"),
            reader.GetOrdinal("narrative")
        ];

        var sig = reader.GetString(_sigOrdinals[0]);
        return new DashboardSignatureEvent
        {
            SignatureId      = sig,
            PrimarySignature = sig,
            Timestamp        = DateTime.Parse(reader.GetString(_sigOrdinals[1])),
            RiskBand         = reader.IsDBNull(_sigOrdinals[2])  ? "Unknown" : reader.GetString(_sigOrdinals[2]),
            BotName          = reader.IsDBNull(_sigOrdinals[3])  ? null : reader.GetString(_sigOrdinals[3]),
            BotType          = reader.IsDBNull(_sigOrdinals[4])  ? null : reader.GetString(_sigOrdinals[4]),
            IsKnownBot       = reader.GetInt32(_sigOrdinals[5]) == 1,
            BotProbability   = reader.GetDouble(_sigOrdinals[6]),
            Confidence       = reader.GetDouble(_sigOrdinals[7]),
            Action           = reader.IsDBNull(_sigOrdinals[8])  ? null : reader.GetString(_sigOrdinals[8]),
            HitCount         = reader.GetInt32(_sigOrdinals[9]),
            ProcessingTimeMs = reader.GetDouble(_sigOrdinals[10]),
            ThreatScore      = reader.IsDBNull(_sigOrdinals[11]) ? 0 : reader.GetDouble(_sigOrdinals[11]),
            ThreatBand       = reader.IsDBNull(_sigOrdinals[12]) ? null : reader.GetString(_sigOrdinals[12]),
            Narrative        = reader.IsDBNull(_sigOrdinals[13]) ? null : reader.GetString(_sigOrdinals[13]),
            RiskJustification = SafeGetString(reader, "risk_justification")
        };
    }

    public async Task<InvestigationResult> GetInvestigationAsync(InvestigationFilter filter, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        // Empty entity value or explicit "all" => no entity gate, the free-text filters
        // do the narrowing. Previously this defaulted to WHERE 1=0 and the Investigate
        // tab reported "No detections found." for every search that didn't drill into
        // a specific entity.
        var hasEntityValue = !string.IsNullOrWhiteSpace(filter.EntityValue);
        var whereClause = (filter.EntityType, hasEntityValue) switch
        {
            ("all", _)        => "1=1",
            (_, false)        => "1=1",
            ("signature", _)  => "d.signature = @Value",
            ("country", _)    => "d.country_code = @Value",
            ("path", _)       => "d.path LIKE @Value",
            ("ua_family", _)  => "d.user_agent_raw LIKE @Value || '%'",
            _                 => "1=0"
        };

        var timeFilter = "";
        if (filter.Start.HasValue) timeFilter += " AND d.timestamp >= @Start";
        if (filter.End.HasValue)   timeFilter += " AND d.timestamp <= @End";

        // Free-text filter inputs from the Investigate bar. SQLite has no
        // ip_search_hmac column, so IpHmac is honored only by the Postgres store.
        var extraFilter = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(filter.EndpointPath)) extraFilter.Append(" AND d.path LIKE @EndpointPath");
        if (!string.IsNullOrWhiteSpace(filter.UserAgent))    extraFilter.Append(" AND d.user_agent_raw LIKE @UserAgent");
        if (!string.IsNullOrWhiteSpace(filter.Country))      extraFilter.Append(" AND d.country_code = @Country");
        if (!string.IsNullOrWhiteSpace(filter.BotName))      extraFilter.Append(" AND d.bot_name LIKE @BotName");

        var baseSql = $"FROM detections d WHERE {whereClause}{timeFilter}{extraFilter}";
        var paramValue = filter.EntityType == "path" ? $"%{filter.EntityValue}%" : filter.EntityValue;

        void BindFilters(SqliteCommand cmd)
        {
            cmd.Parameters.AddWithValue("@Value", paramValue);
            cmd.Parameters.AddWithValue("@botFloor", _botFloor);
            if (filter.Start.HasValue) cmd.Parameters.AddWithValue("@Start", filter.Start.Value.ToString("o"));
            if (filter.End.HasValue)   cmd.Parameters.AddWithValue("@End",   filter.End.Value.ToString("o"));
            if (!string.IsNullOrWhiteSpace(filter.EndpointPath)) cmd.Parameters.AddWithValue("@EndpointPath", $"%{filter.EndpointPath.Trim()}%");
            if (!string.IsNullOrWhiteSpace(filter.UserAgent))    cmd.Parameters.AddWithValue("@UserAgent",    $"%{filter.UserAgent.Trim()}%");
            if (!string.IsNullOrWhiteSpace(filter.Country))      cmd.Parameters.AddWithValue("@Country",      filter.Country.Trim());
            if (!string.IsNullOrWhiteSpace(filter.BotName))      cmd.Parameters.AddWithValue("@BotName",      $"%{filter.BotName.Trim()}%");
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var txn = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

        // ── Summary ──────────────────────────────────────────────────────────
        await using var summaryCmd = conn.CreateCommand();
        // Fused rows weight the totals by their counters; the risk-band split
        // attributes a fused row's hit_count to its representative's band (the
        // fusion key doesn't carry risk_band — a documented approximation on
        // old low-importance rows).
        summaryCmd.CommandText = $"""
            SELECT
                SUM({FusedTotalExpr}) AS TotalDetections,
                MIN(timestamp) AS FirstSeen,
                MAX(timestamp) AS LastSeen,
                SUM(CASE WHEN fused = 1 AND risk_band = 'high'   THEN hit_count WHEN risk_band = 'high'   THEN 1 ELSE 0 END) AS HighRisk,
                SUM(CASE WHEN fused = 1 AND risk_band = 'medium' THEN hit_count WHEN risk_band = 'medium' THEN 1 ELSE 0 END) AS MediumRisk,
                SUM(CASE WHEN fused = 1 AND risk_band = 'low'    THEN hit_count WHEN risk_band = 'low'    THEN 1 ELSE 0 END) AS LowRisk
            {baseSql}
            """;
        BindFilters(summaryCmd);

        InvestigationSummary summary;
        await using (var r = await summaryCmd.ExecuteReaderAsync(ct))
        {
            if (await r.ReadAsync(ct))
            {
                summary = new InvestigationSummary
                {
                    TotalDetections = r.IsDBNull(0) ? 0 : r.GetInt64(0),
                    FirstSeen  = r.IsDBNull(1) ? null : DateTime.Parse(r.GetString(1)),
                    LastSeen   = r.IsDBNull(2) ? null : DateTime.Parse(r.GetString(2)),
                    HighRisk   = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                    MediumRisk = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                    LowRisk    = r.IsDBNull(5) ? 0 : r.GetInt32(5)
                };
            }
            else
            {
                summary = new InvestigationSummary();
            }
        }

        // ── Detections (paginated) ────────────────────────────────────────────
        await using var detCmd = conn.CreateCommand();
        detCmd.CommandText = $"""
            SELECT
                d.signature, d.timestamp, d.method, d.path,
                d.is_bot, d.bot_probability, d.confidence, d.risk_band,
                d.bot_name, d.bot_type, d.action, d.country_code,
                d.processing_time_ms, d.status_code, d.user_agent_raw,
                d.threat_score, d.threat_band,
                d.domain, d.referrer_host, d.ua_device_class
            {baseSql} AND d.fused = 0
            ORDER BY d.timestamp DESC
            LIMIT @Limit OFFSET @Offset
            """;
        BindFilters(detCmd);
        detCmd.Parameters.AddWithValue("@Limit",  filter.Limit);
        detCmd.Parameters.AddWithValue("@Offset", filter.Offset);

        var detections = new List<DashboardDetectionEvent>();
        await using (var r = await detCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                detections.Add(new DashboardDetectionEvent
                {
                    PrimarySignature = r.GetString(0),
                    RequestId        = r.GetString(0),
                    Timestamp        = DateTime.Parse(r.GetString(1)),
                    Method           = r.IsDBNull(2)  ? "" : r.GetString(2),
                    Path             = r.IsDBNull(3)  ? "/" : r.GetString(3),
                    IsBot            = r.GetInt32(4) == 1,
                    BotProbability   = r.GetDouble(5),
                    Confidence       = r.GetDouble(6),
                    RiskBand         = r.IsDBNull(7)  ? "Unknown" : r.GetString(7),
                    BotName          = r.IsDBNull(8)  ? null : r.GetString(8),
                    BotType          = r.IsDBNull(9)  ? null : r.GetString(9),
                    Action           = r.IsDBNull(10) ? null : r.GetString(10),
                    CountryCode      = r.IsDBNull(11) ? null : r.GetString(11),
                    ProcessingTimeMs = r.IsDBNull(12) ? 0    : r.GetDouble(12),
                    StatusCode       = r.IsDBNull(13) ? 0    : r.GetInt32(13),
                    UserAgentRaw     = r.IsDBNull(14) ? null : r.GetString(14),
                    ThreatScore      = r.IsDBNull(15) ? 0    : r.GetDouble(15),
                    ThreatBand       = r.IsDBNull(16) ? null : r.GetString(16),
                    Domain           = r.IsDBNull(17) ? null : r.GetString(17),
                    ReferrerHost     = r.IsDBNull(18) ? null : r.GetString(18),
                    UaDeviceClass    = r.IsDBNull(19) ? null : r.GetString(19)
                });
            }
        }

        // ── Signatures (distinct within result set) ───────────────────────────
        await using var sigCmd = conn.CreateCommand();
        sigCmd.CommandText = $"""
            SELECT
                s.signature, s.hit_count, s.bot_name, s.bot_type,
                s.risk_band, (s.bot_probability >= @botFloor) AS is_bot, s.last_seen
            FROM signatures s
            WHERE s.signature IN (SELECT DISTINCT d.signature {baseSql})
            ORDER BY s.hit_count DESC
            LIMIT 50
            """;
        BindFilters(sigCmd);

        var signatures = new List<SignatureSummary>();
        await using (var r = await sigCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                signatures.Add(new SignatureSummary
                {
                    PrimarySignature = r.GetString(0),
                    HitCount         = r.IsDBNull(1) ? 0     : r.GetInt32(1),
                    BotName          = r.IsDBNull(2) ? null  : r.GetString(2),
                    BotType          = r.IsDBNull(3) ? null  : r.GetString(3),
                    RiskBand         = r.IsDBNull(4) ? null  : r.GetString(4),
                    IsKnownBot       = !r.IsDBNull(5) && r.GetInt32(5) == 1,
                    LastSeen         = r.IsDBNull(6) ? default : DateTime.Parse(r.GetString(6))
                });
            }
        }

        // ── Endpoint stats ────────────────────────────────────────────────────
        await using var epCmd = conn.CreateCommand();
        epCmd.CommandText = $"""
            SELECT
                d.method, d.path,
                COUNT(*) AS Count,
                AVG(d.bot_probability) AS AvgBotProb
            -- Drill-down: fused rows lost their (method, path) shape.
            {baseSql} AND d.fused = 0
            GROUP BY d.method, d.path
            ORDER BY Count DESC
            LIMIT 50
            """;
        BindFilters(epCmd);

        var endpoints = new List<EndpointStat>();
        await using (var r = await epCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                endpoints.Add(new EndpointStat
                {
                    Method             = r.IsDBNull(0) ? "GET" : r.GetString(0),
                    Path               = r.IsDBNull(1) ? "/"   : r.GetString(1),
                    Count              = r.IsDBNull(2) ? 0     : r.GetInt32(2),
                    AvgBotProbability  = r.IsDBNull(3) ? 0     : r.GetDouble(3)
                });
            }
        }

        // ── Country breakdown ─────────────────────────────────────────────────
        await using var ctryCmd = conn.CreateCommand();
        // Country IS part of the fusion key, so fused rows' counts are exact here.
        ctryCmd.CommandText = $"""
            SELECT
                d.country_code,
                SUM({FusedTotalExpr}) AS Count,
                SUM({FusedBotsExpr(null)}) AS BotCount
            {baseSql} AND d.country_code IS NOT NULL
            GROUP BY d.country_code
            ORDER BY Count DESC
            LIMIT 50
            """;
        BindFilters(ctryCmd);

        var countries = new List<CountryStat>();
        await using (var r = await ctryCmd.ExecuteReaderAsync(ct))
        {
            while (await r.ReadAsync(ct))
            {
                countries.Add(new CountryStat
                {
                    CountryCode = r.IsDBNull(0) ? "XX" : r.GetString(0),
                    Count       = r.IsDBNull(1) ? 0    : r.GetInt32(1),
                    BotCount    = r.IsDBNull(2) ? 0    : r.GetInt32(2)
                });
            }
        }

        return new InvestigationResult
        {
            Summary          = summary,
            Detections       = detections,
            Signatures       = signatures,
            EndpointStats    = endpoints,
            CountryBreakdown = countries,
            TotalCount       = (int)summary.TotalDetections
        };
    }

    /// <summary>
    ///     Time-series of UA versions for the given family over the last <paramref name="hours"/>.
    ///     SQLite doesn't carry a parsed-family column, so we read recent raw user_agents in
    ///     the time window and parse them in-process via <see cref="UserAgentParser"/>. Bucketed
    ///     by hour. Acceptable on FOSS single-binary deployments; commercial PG has a direct
    ///     JSON query that doesn't load rows into memory.
    /// </summary>
    public async Task<List<UserAgentVersionBucket>> GetUserAgentVersionHistoryAsync(
        string family, int hours = 168, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(family)) return [];
        await EnsureInitializedAsync(ct);
        var sinceUtc = DateTime.UtcNow.AddHours(-Math.Clamp(hours, 1, 24 * 90));
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, user_agent_raw FROM detections
             WHERE user_agent_raw IS NOT NULL AND user_agent_raw <> ''
               AND timestamp >= @since
            """;
        cmd.Parameters.AddWithValue("@since", sinceUtc.ToString("O"));

        var buckets = new Dictionary<(DateTime Bucket, string Version), int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!DateTime.TryParse(reader.GetString(0), out var ts)) continue;
            var ua = reader.GetString(1);
            var parsed = Mostlylucid.BotDetection.Helpers.UserAgentParser.Parse(ua);
            if (!string.Equals(parsed.Family, family, StringComparison.OrdinalIgnoreCase)) continue;
            if (string.IsNullOrEmpty(parsed.Version)) continue;
            var bucket = new DateTime(ts.Year, ts.Month, ts.Day, ts.Hour, 0, 0, DateTimeKind.Utc);
            var key = (bucket, parsed.Version);
            buckets[key] = (buckets.TryGetValue(key, out var v) ? v : 0) + 1;
        }

        return buckets
            .Select(kv => new UserAgentVersionBucket { Bucket = kv.Key.Bucket, Version = kv.Key.Version, Hits = kv.Value })
            .OrderBy(b => b.Bucket).ThenByDescending(b => b.Hits)
            .ToList();
    }

    public async Task<List<UserAgentSearchResult>> SearchUserAgentsAsync(string query, int limit = 20)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT user_agent_raw, signature, bot_probability, timestamp, bot_name
            FROM detections
            WHERE user_agent_raw LIKE @query
            ORDER BY timestamp DESC LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@query", $"%{query}%");
        cmd.Parameters.AddWithValue("@limit", Math.Clamp(limit, 1, 100));

        var results = new List<UserAgentSearchResult>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new UserAgentSearchResult
            {
                UserAgent = reader.IsDBNull(0) ? "" : reader.GetString(0),
                Signature = reader.GetString(1),
                BotProbability = reader.GetDouble(2),
                Timestamp = DateTime.Parse(reader.GetString(3)),
                BotName = reader.IsDBNull(4) ? null : reader.GetString(4)
            });
        }
        return results;
    }

    // ─── UA stats helpers ────────────────────────────────────────────────

    private static readonly Regex UaFamilyRegex = new(
        @"^(?<family>[A-Za-z][A-Za-z0-9 _-]*)/?(?<version>\d[\d.]*)?",
        RegexOptions.Compiled);

    private static async Task UpsertUserAgentStatsAsync(SqliteConnection conn, string? strippedUa, bool isBot)
    {
        if (string.IsNullOrWhiteSpace(strippedUa)) return;

        var (family, version) = ParseUaFamily(strippedUa);
        if (string.IsNullOrEmpty(family)) return;

        var now = DateTime.UtcNow.ToString("O");

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO user_agent_stats (ua_family, ua_version, ua_os, is_bot, first_seen, last_seen, hit_count)
            VALUES (@family, @version, @os, @isBot, @now, @now, 1)
            ON CONFLICT(ua_family, ua_version, ua_os) DO UPDATE SET
                last_seen = @now,
                hit_count = hit_count + 1,
                is_bot = MAX(is_bot, @isBot)
            """;
        cmd.Parameters.AddWithValue("@family", family);
        cmd.Parameters.AddWithValue("@version", version ?? "");
        cmd.Parameters.AddWithValue("@os", ""); // OS extraction can be added later
        cmd.Parameters.AddWithValue("@isBot", isBot ? 1 : 0);
        cmd.Parameters.AddWithValue("@now", now);

        await cmd.ExecuteNonQueryAsync();
    }

    private static (string? Family, string? Version) ParseUaFamily(string ua)
    {
        // Try common browser patterns first
        if (ua.Contains("Firefox/", StringComparison.Ordinal))
            return ("Firefox", ExtractToken(ua, "Firefox/"));
        if (ua.Contains("Edg/", StringComparison.Ordinal))
            return ("Edge", ExtractToken(ua, "Edg/"));
        if (ua.Contains("OPR/", StringComparison.Ordinal))
            return ("Opera", ExtractToken(ua, "OPR/"));
        if (ua.Contains("Chrome/", StringComparison.Ordinal) && !ua.Contains("Chromium", StringComparison.Ordinal))
            return ("Chrome", ExtractToken(ua, "Chrome/"));
        if (ua.Contains("Safari/", StringComparison.Ordinal) && ua.Contains("Version/", StringComparison.Ordinal))
            return ("Safari", ExtractToken(ua, "Version/"));

        // Fallback: first token of the UA (handles "MyBot/1.0 (...)" patterns)
        var match = UaFamilyRegex.Match(ua);
        if (match.Success)
            return (match.Groups["family"].Value.Trim(), match.Groups["version"].Value);

        return (null, null);
    }

    private static string? ExtractToken(string ua, string token)
    {
        var idx = ua.IndexOf(token, StringComparison.Ordinal);
        if (idx < 0) return null;
        var start = idx + token.Length;
        var end = start;
        while (end < ua.Length && (char.IsDigit(ua[end]) || ua[end] == '.'))
            end++;
        if (end == start) return null;
        var full = ua[start..end];
        var dot = full.IndexOf('.');
        return dot > 0 ? full[..dot] : full;
    }

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

    /// <summary>
    ///     Safe nullable int64 read that handles missing or NULL columns.
    ///     Uses <c>Convert.ToInt64</c> rather than <c>GetInt64</c> to avoid a type-mismatch
    ///     when SQLite returns an aggregate (e.g. SUM) as REAL instead of INTEGER.
    /// </summary>
    private static long? SafeGetInt64Nullable(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? null : Convert.ToInt64(reader.GetValue(ordinal));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null; // Column doesn't exist yet (pre-migration DB)
        }
    }

    private static int SafeGetInt32(SqliteDataReader reader, string column)
    {
        try
        {
            var ordinal = reader.GetOrdinal(column);
            return reader.IsDBNull(ordinal) ? 0 : Convert.ToInt32(reader.GetValue(ordinal));
        }
        catch (ArgumentOutOfRangeException)
        {
            return 0; // Column doesn't exist yet (pre-migration DB)
        }
    }

    /// <summary>
    ///     Rows deleted per retention DELETE statement. Bounding the batch keeps
    ///     each statement's write-lock + WAL footprint small on a busy database;
    ///     the loop drains the backlog across round-trips (dbreview- 2026-08-14).
    /// </summary>
    private const int RetentionBatchSize = 5000;

    public async Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            return await PruneDetectionsBatchedAsync(conn, cutoff, ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Bounded-loop retention delete: each statement removes at most
    ///     <see cref="RetentionBatchSize"/> rows (index-served via
    ///     <c>idx_det_timestamp</c>), looping until the backlog is gone. The old
    ///     single unbounded DELETE ran one long statement per prune; under 7-day
    ///     retention on a busy site that meant a big lock + WAL spike on every
    ///     Tick1h and every boot.
    /// </summary>
    private async Task<int> PruneDetectionsBatchedAsync(
        SqliteConnection conn, DateTime cutoff, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM detections
             WHERE id IN (
                 SELECT id FROM detections
                  WHERE timestamp < @cutoff
                  ORDER BY timestamp
                  LIMIT @batch)
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        cmd.Parameters.AddWithValue("@batch", RetentionBatchSize);

        var total = 0;
        while (true)
        {
            var deleted = await cmd.ExecuteNonQueryAsync(ct);
            total += deleted;
            if (deleted < RetentionBatchSize) break;
        }
        return total;
    }

    public async Task<int> FoldAgedDetectionsAsync(
        DateTime hotCutoff,
        DateTime fullAbsorptionCutoff,
        double importanceFloor,
        int batchSize,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            var total = 0;

            // Pass 1 — FUSION (sparse aggregates): the low-importance aged region
            // past HotWindow collapses into one summary row per
            // (signature, hour-bucket, domain, country, bot_type). Importance
            // decides WHO fuses, the hour bucket decides the FUSION GRANULARITY.
            // Absorbed rows are DELETEd — this is what actually bounds table
            // growth (the fold no longer just nulls detail, it removes rows).
            total += await FuseBatchAsync(
                conn, hotCutoff, importanceFloor, batchSize, ct);

            // Pass 2 — FULL ABSORPTION: rows past FullAbsorptionAge that are NOT
            // fusion-eligible (high importance, enforcement, or threat rows)
            // lose their detail columns. The two passes PARTITION the aged
            // population — pass 2 explicitly skips fusion-eligible rows — so
            // pass 2 can never null a low-importance row's detail (and its
            // method drain marker) before pass 1 fuses it. Fused rows are
            // skipped by the method IS NOT NULL drain marker. The per-request
            // detail columns the fold nulls. Everything the dashboard aggregates
            // on (counts, bot_probability, risk_band, action, threat_score,
            // domain/host, and the numeric KPI columns response_bytes/
            // processing_time_ms) is deliberately untouched, so reads return
            // identical shapes with or without compression. SQLite treats
            // re-nulling a NULL column as a no-op, so the pass is idempotent.
            total += await FoldBatchAsync(
                conn, FoldDetailSet, fullAbsorptionCutoff, importanceFloor, batchSize, ct);

            return total;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>
    ///     Session de-resolution (ladder-on-sessions ruling, operator 2026-08-15): the
    ///     sessions table is a first-class row unit WITHIN
    ///     <see cref="TemporalStoreOptions.SessionRowHorizon"/> — the Sessions view reads
    ///     live rows. Past the horizon, each row is de-resolved: its data entered the
    ///     window aggregates ONCE at the hour boundary (this sweep's fold of the same
    ///     requests); the de-resolution VERIFIES the signature's aggregate coverage
    ///     exists (a guarded one-time backfill folds the row's summary in ONLY where the
    ///     coverage is absent — a missed-fold gap, never a double count) and DELETES the
    ///     row. The backfill + delete are on separate SQLite databases (detections vs
    ///     sessions.db), so there is no cross-DB transaction — the guarded coverage check
    ///     makes the pass self-healing: a crash between backfill and delete leaves the
    ///     row plus its aggregate, and the next pass sees the coverage and just deletes.
    ///     The table stays flat by construction (bounded by sessions-within-horizon).
    /// </summary>
    public async Task DeResolveSessionsAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);

        var cutoff = nowUtc - _temporalStore.SessionRowHorizon;
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        var sessionsDb = Path.Combine(
            Path.GetDirectoryName(_options.DatabasePath) ?? AppContext.BaseDirectory, "sessions.db");
        await using var sessionConn = new SqliteConnection($"Data Source={sessionsDb};Cache=Shared;Pooling=true");
        await sessionConn.OpenAsync(ct);

        // The sessions table may not exist (a store-only host, or a DB that predates the
        // ladder) — the pass is a no-op then.
        await using (var probe = sessionConn.CreateCommand())
        {
            probe.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'sessions'";
            if (Convert.ToInt64(await probe.ExecuteScalarAsync(ct)) == 0) return;
        }

        // Aged rows, oldest first, batch-capped (the fold's batch knob bounds one pass).
        var aged = new List<(long Id, string Signature, DateTime StartedAt, bool IsBot, double? AvgMs, string? Paths, string? Domain, string? Host, string? RiskBand)>();
        await using (var agedCmd = sessionConn.CreateCommand())
        {
            agedCmd.CommandText = """
                SELECT id, signature, started_at, ended_at, request_count, is_bot,
                       avg_processing_time_ms, paths_json, domain, host, risk_band
                FROM sessions
                WHERE ended_at < @cutoff
                ORDER BY ended_at
                LIMIT @batch
                """;
            agedCmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
            agedCmd.Parameters.AddWithValue("@batch", _temporalStore.FoldBatchSize);
            await using var reader = await agedCmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                aged.Add((
                    reader.GetInt64(0), reader.GetString(1),
                    DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    reader.GetInt32(5) != 0,
                    reader.IsDBNull(6) ? null : reader.GetDouble(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9),
                    reader.IsDBNull(10) ? null : reader.GetString(10)));
            }
        }
        if (aged.Count == 0) return;

        var deleted = 0;
        await using (var delTx = (SqliteTransaction)await sessionConn.BeginTransactionAsync(ct))
        {
            foreach (var row in aged)
            {
                if (ct.IsCancellationRequested) break;
                var hourStart = new DateTime(row.StartedAt.Year, row.StartedAt.Month, row.StartedAt.Day,
                    row.StartedAt.Hour, 0, 0, DateTimeKind.Utc);
                var hourEnd = hourStart.AddHours(1);

                // Coverage check: this sweep's fold wrote hour-anchored fused rows for
                // the signature in the session's hour span — OR a prior pass's SESSION
                // backfill row (itself the coverage; without this arm a second pass
                // would double-count).
                await using var covCmd = conn.CreateCommand();
                covCmd.CommandText = """
                    SELECT COUNT(*) FROM detections
                     WHERE (fused = 1 OR method = 'SESSION') AND signature = @sig
                       AND timestamp >= @from AND timestamp < @to
                    """;
                covCmd.Parameters.AddWithValue("@sig", row.Signature);
                covCmd.Parameters.AddWithValue("@from", hourStart.ToString("O"));
                covCmd.Parameters.AddWithValue("@to", hourEnd.ToString("O"));
                var covered = Convert.ToInt64(await covCmd.ExecuteScalarAsync(ct));

                if (covered == 0)
                {
                    // Guarded one-time backfill: the session's data never reached the
                    // aggregates (a missed fold — not a double count: coverage was
                    // absent). One sparse-but-valid aggregate row anchored at the
                    // session's start hour. request_id names the source row so a
                    // duplicate backfill attempt is identifiable.
                    await using var backCmd = conn.CreateCommand();
                    backCmd.CommandText = """
                        INSERT INTO detections (
                            timestamp, signature, method, path, is_bot,
                            bot_probability, confidence, risk_band, processing_time_ms,
                            domain, host
                        ) VALUES (
                            @ts, @sig, 'SESSION', '/', @isBot,
                            0, 0, @risk, @ms,
                            @domain, @host
                        )
                        """;
                    backCmd.Parameters.AddWithValue("@ts", hourStart.ToString("O"));
                    backCmd.Parameters.AddWithValue("@sig", row.Signature);
                    backCmd.Parameters.AddWithValue("@isBot", row.IsBot ? 1 : 0);
                    backCmd.Parameters.AddWithValue("@risk", (object?)row.RiskBand ?? "Low");
                    backCmd.Parameters.AddWithValue("@ms", row.AvgMs ?? 0);
                    backCmd.Parameters.AddWithValue("@domain", (object?)row.Domain ?? DBNull.Value);
                    backCmd.Parameters.AddWithValue("@host", (object?)row.Host ?? DBNull.Value);
                    await backCmd.ExecuteNonQueryAsync(ct);
                }

                await using var delCmd = sessionConn.CreateCommand();
                delCmd.Transaction = delTx;
                delCmd.CommandText = "DELETE FROM sessions WHERE id = @id";
                delCmd.Parameters.AddWithValue("@id", row.Id);
                await delCmd.ExecuteNonQueryAsync(ct);
                deleted++;
            }
            await delTx.CommitAsync(ct);
        }

        if (deleted > 0)
        {
            _logger.LogInformation(
                "Session de-resolution: {Count} session row(s) past the {Horizon} horizon de-resolved into the window aggregates",
                deleted, _temporalStore.SessionRowHorizon);
        }
    }

    // The per-request detail columns the fold nulls (pass 2) and the fusion
    // clears on the surviving representative row. Mirrors DetectionImportance's
    // enforcement keyword set — a test pins the two together.
    private const string FoldDetailSet =
        "method = NULL, path = NULL, user_agent_raw = NULL, referrer_host = NULL, " +
        "ua_device_class = NULL, risk_justification = NULL";

    /// <summary>
    ///     Fusion exemption predicates, built from
    ///     <see cref="DetectionImportance.EnforcementActionKeywords"/> — the
    ///     single source of truth for the keyword set, so the C# gate
    ///     (IsEnforcementAction) and the SQL gates can never drift apart.
    ///     Enforcement rows (the audit trail — blocked/challenged/throttled/
    ///     honeypot) and rows at/above the fusion threat ceiling (the evidence
    ///     feed) are never fused; they keep their own row and detail until full
    ///     absorption.
    /// </summary>
    private static readonly string NonEnforcementPredicate = BuildNotEnforcementPredicate();
    private static readonly string EnforcementPredicate = BuildEnforcementPredicate();

    private static string BuildNotEnforcementPredicate()
    {
        var nots = DetectionImportance.EnforcementActionKeywords
            .Select(k => $"action NOT LIKE '%{k}%'");
        return $"(action IS NULL OR ({string.Join(" AND ", nots)}))";
    }

    private static string BuildEnforcementPredicate()
    {
        var ors = DetectionImportance.EnforcementActionKeywords
            .Select(k => $"action LIKE '%{k}%'");
        return $"({string.Join(" OR ", ors)})";
    }

    private async Task<int> FuseBatchAsync(
        SqliteConnection conn,
        DateTime hotCutoff,
        double importanceFloor,
        int batchSize,
        CancellationToken ct)
    {
        // Candidate drain: lowest-importance detail-carrying rows past the hot
        // window, excluding enforcement + threat rows (those never fuse).
        await using var sel = conn.CreateCommand();
        sel.CommandText = $"""
            SELECT id, signature, timestamp, domain, country_code, bot_type,
                   importance_weight, bot_probability, response_bytes, processing_time_ms
            FROM detections
            WHERE timestamp < @hotCutoff
              AND importance_weight < @floor
              -- Drain marker: only rows that still hold their detail fuse.
              AND method IS NOT NULL
              AND {NonEnforcementPredicate}
              AND (threat_score IS NULL OR threat_score < @threatCeiling)
            ORDER BY importance_weight ASC, timestamp ASC
            LIMIT @batch
            """;
        sel.Parameters.AddWithValue("@hotCutoff", hotCutoff.ToString("O"));
        sel.Parameters.AddWithValue("@floor", importanceFloor);
        sel.Parameters.AddWithValue("@threatCeiling", _temporalStore.FusionThreatCeiling);
        sel.Parameters.AddWithValue("@batch", Math.Max(batchSize, 1));

        var candidates = new List<FusionCandidate>();
        await using (var reader = await sel.ExecuteReaderAsync(ct))
        {
            while (await reader.ReadAsync(ct))
            {
                candidates.Add(new FusionCandidate(
                    Id: reader.GetInt64(0),
                    Signature: reader.GetString(1),
                    Timestamp: DateTime.Parse(reader.GetString(2), null, System.Globalization.DateTimeStyles.RoundtripKind),
                    Domain: reader.IsDBNull(3) ? null : reader.GetString(3),
                    CountryCode: reader.IsDBNull(4) ? null : reader.GetString(4),
                    BotType: reader.IsDBNull(5) ? null : reader.GetString(5),
                    Weight: reader.GetDouble(6),
                    BotProbability: reader.GetDouble(7),
                    ResponseBytes: reader.IsDBNull(8) ? (long?)null : reader.GetInt64(8),
                    ProcessingTimeMs: reader.IsDBNull(9) ? (double?)null : reader.GetDouble(9)));
            }
        }

        if (candidates.Count == 0) return 0;

        // Group by (signature, hour-bucket, domain, country, bot_type). The
        // representative is the highest-importance member (best attribute
        // fidelity); the counters are exact over the absorbed rows.
        var groups = new Dictionary<FusionKey, List<FusionCandidate>>();
        foreach (var c in candidates)
        {
            var hourBucket = c.Timestamp.Ticks / TimeSpan.TicksPerHour;
            var key = new FusionKey(c.Signature, hourBucket, c.Domain, c.CountryCode, c.BotType);
            if (!groups.TryGetValue(key, out var list))
                groups[key] = list = new List<FusionCandidate>();
            list.Add(c);
        }

        await using var txn = (SqliteTransaction)await conn.BeginTransactionAsync(ct);
        var fused = 0;

        foreach (var (key, members) in groups)
        {
            var representative = members.MaxBy(c => c.Weight)!;
            var hitCount = members.Count;
            var botCount = members.Count(c => c.BotProbability >= _botFloor);
            var bytesSum = members.Sum(c => c.ResponseBytes ?? 0);
            var msSum = members.Sum(c => c.ProcessingTimeMs ?? 0);
            var msMax = members.Max(c => c.ProcessingTimeMs ?? 0);
            var bucketStart = new DateTime(key.HourBucket * TimeSpan.TicksPerHour, DateTimeKind.Utc);

            // Merge into an existing fused row of the same key when one exists
            // (e.g. a group split across two fold ticks): add to its counters
            // instead of creating a second summary row. SQLite IS comparisons
            // handle the NULL domain/country/bot_type members exactly.
            await using var lookup = conn.CreateCommand();
            lookup.Transaction = txn;
            lookup.CommandText = """
                SELECT id FROM detections
                 WHERE fused = 1 AND signature = @sig AND timestamp = @bucket
                   AND domain IS @domain AND country_code IS @country AND bot_type IS @botType
                 LIMIT 1
                """;
            lookup.Parameters.AddWithValue("@sig", key.Signature);
            lookup.Parameters.AddWithValue("@bucket", bucketStart.ToString("O"));
            lookup.Parameters.AddWithValue("@domain", (object?)key.Domain ?? DBNull.Value);
            lookup.Parameters.AddWithValue("@country", (object?)key.CountryCode ?? DBNull.Value);
            lookup.Parameters.AddWithValue("@botType", (object?)key.BotType ?? DBNull.Value);
            var existingId = await lookup.ExecuteScalarAsync(ct);

            await using var upd = conn.CreateCommand();
            upd.Transaction = txn;
            if (existingId is not null)
            {
                upd.CommandText = """
                    UPDATE detections
                       SET hit_count = hit_count + @hit, bot_count = bot_count + @bots,
                           bytes_sum = bytes_sum + @bytes, ms_sum = ms_sum + @ms,
                           ms_max = MAX(ms_max, @msMax)
                     WHERE id = @id
                    """;
                upd.Parameters.AddWithValue("@hit", hitCount);
                upd.Parameters.AddWithValue("@bots", botCount);
                upd.Parameters.AddWithValue("@bytes", bytesSum);
                upd.Parameters.AddWithValue("@ms", msSum);
                upd.Parameters.AddWithValue("@msMax", msMax);
                upd.Parameters.AddWithValue("@id", (long)existingId);
            }
            else
            {
                // The representative becomes the summary row: counters set,
                // timestamp anchored to the bucket start, detail cleared.
                upd.CommandText = $"""
                    UPDATE detections
                       SET fused = 1,
                           hit_count = @hit, bot_count = @bots,
                           bytes_sum = @bytes, ms_sum = @ms, ms_max = @msMax,
                           timestamp = @bucket,
                           {FoldDetailSet}
                     WHERE id = @id
                    """;
                upd.Parameters.AddWithValue("@hit", hitCount);
                upd.Parameters.AddWithValue("@bots", botCount);
                upd.Parameters.AddWithValue("@bytes", bytesSum);
                upd.Parameters.AddWithValue("@ms", msSum);
                upd.Parameters.AddWithValue("@msMax", msMax);
                upd.Parameters.AddWithValue("@bucket", bucketStart.ToString("O"));
                upd.Parameters.AddWithValue("@id", representative.Id);
            }
            await upd.ExecuteNonQueryAsync(ct);

            // Absorbed rows are deleted — this is the row-count reduction.
            await using var del = conn.CreateCommand();
            del.Transaction = txn;
            del.CommandText = "DELETE FROM detections WHERE id = @id";
            var delId = del.CreateParameter();
            delId.ParameterName = "@id";
            del.Parameters.Add(delId);
            foreach (var member in members)
            {
                if (existingId is not null || member.Id != representative.Id)
                {
                    delId.Value = member.Id;
                    await del.ExecuteNonQueryAsync(ct);
                }
            }

            fused += hitCount;
        }

        await txn.CommitAsync(ct);
        return fused;
    }

    /// <summary>
    ///     Full-absorption pass (pass 2): nulls the detail of rows past
    ///     <paramref name="cutoff"/> that are NOT fusion-eligible — high
    ///     importance (weight at/above the floor), enforcement, or threat rows.
    ///     Fusion-eligible rows are explicitly excluded: they belong to pass 1's
    ///     drain and must keep their <c>method</c> marker until fused, or the
    ///     two passes would fight over the same population.
    /// </summary>
    private async Task<int> FoldBatchAsync(
        SqliteConnection conn,
        string detailSet,
        DateTime cutoff,
        double importanceFloor,
        int batchSize,
        CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            UPDATE detections
               SET {detailSet}
             WHERE id IN (
                   SELECT id
                     FROM detections
                    WHERE timestamp < @cutoff
                      -- Drain marker: only rows that still hold their detail fold.
                      -- Without this the lowest-importance rows re-match forever
                      -- (folding them is a no-op) and the batch never advances.
                      AND method IS NOT NULL
                      -- Partition: pass 2 owns everything pass 1 (fusion) does
                      -- NOT claim — high-importance, enforcement, or threat rows.
                      AND (importance_weight >= @floor
                           OR {EnforcementPredicate}
                           OR (threat_score IS NOT NULL AND threat_score >= @threatCeiling))
                    ORDER BY importance_weight ASC, timestamp ASC
                    LIMIT @batch)
            """;
        cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
        cmd.Parameters.AddWithValue("@floor", importanceFloor);
        cmd.Parameters.AddWithValue("@threatCeiling", _temporalStore.FusionThreatCeiling);
        cmd.Parameters.AddWithValue("@batch", Math.Max(batchSize, 1));
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    private readonly record struct FusionCandidate(
        long Id,
        string Signature,
        DateTime Timestamp,
        string? Domain,
        string? CountryCode,
        string? BotType,
        double Weight,
        double BotProbability,
        long? ResponseBytes,
        double? ProcessingTimeMs);

    private readonly record struct FusionKey(
        string Signature,
        long HourBucket,
        string? Domain,
        string? CountryCode,
        string? BotType);

    public async Task RecordDegradationSnapshotAsync(
        DegradationSnapshot snapshot, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                INSERT INTO degradation_history
                    (timestamp, latency_p50_ms, latency_p95_ms, rate_5xx, rate_4xx, rate_429, rate_404)
                VALUES (@ts, @p50, @p95, @r5xx, @r4xx, @r429, @r404)
                """;
            cmd.Parameters.AddWithValue("@ts", snapshot.TimestampUtc.ToString("O"));
            cmd.Parameters.AddWithValue("@p50", snapshot.LatencyP50Ms);
            cmd.Parameters.AddWithValue("@p95", snapshot.LatencyP95Ms);
            cmd.Parameters.AddWithValue("@r5xx", snapshot.Latency5xxRate);
            cmd.Parameters.AddWithValue("@r4xx", snapshot.Latency4xxRate);
            cmd.Parameters.AddWithValue("@r429", snapshot.Latency429Rate);
            cmd.Parameters.AddWithValue("@r404", snapshot.NotFoundRate);
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IReadOnlyList<DegradationSnapshot>> GetDegradationHistoryAsync(
        DateTime startTime, DateTime endTime, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT timestamp, latency_p50_ms, latency_p95_ms, rate_5xx, rate_4xx, rate_429, rate_404
              FROM degradation_history
             WHERE timestamp >= @start AND timestamp <= @end
             ORDER BY timestamp ASC
            """;
        cmd.Parameters.AddWithValue("@start", startTime.ToString("O"));
        cmd.Parameters.AddWithValue("@botFloor", _botFloor);
        cmd.Parameters.AddWithValue("@end", endTime.ToString("O"));

        var results = new List<DegradationSnapshot>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            if (!DateTime.TryParse(
                    reader.GetString(0),
                    System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind,
                    out var ts))
            {
                continue;
            }
            results.Add(new DegradationSnapshot(
                TimestampUtc: ts,
                Latency5xxRate: reader.IsDBNull(3) ? 0 : reader.GetDouble(3),
                Latency4xxRate: reader.IsDBNull(4) ? 0 : reader.GetDouble(4),
                Latency429Rate: reader.IsDBNull(5) ? 0 : reader.GetDouble(5),
                LatencyP50Ms:   reader.IsDBNull(1) ? 0 : reader.GetDouble(1),
                LatencyP95Ms:   reader.IsDBNull(2) ? 0 : reader.GetDouble(2),
                NotFoundRate:   reader.IsDBNull(6) ? 0 : reader.GetDouble(6)));
        }
        return results;
    }

    /// <summary>
    ///     Build a SQL fragment of the form
    ///     <c> AND {alias}domain IN (@d0, @d1, ...)</c> and the matching
    ///     <c>(name, value)</c> parameter list. Returns an empty predicate + no
    ///     params when <paramref name="domains"/> is null or empty, so the caller
    ///     can append unconditionally without a null check.
    ///     <para>
    ///         SQLite has no <c>= ANY(array)</c> operator (Postgres does), so the
    ///         filter is emitted as an <c>IN (...)</c> list with one placeholder
    ///         per value. Empty list = no filter, matching the Postgres
    ///         <c>@domains::text[] IS NULL OR ...</c> convention from the task
    ///         spec at the call sites.
    ///     </para>
    /// </summary>
    private static (string Predicate, IReadOnlyList<(string Name, string Value)> Params) BuildDomainPredicate(
        IReadOnlyList<string>? domains, string columnAlias)
    {
        if (domains is null || domains.Count == 0)
            return (string.Empty, Array.Empty<(string, string)>());
        var prefix = string.IsNullOrEmpty(columnAlias) ? string.Empty : columnAlias + ".";
        var placeholders = new List<string>(domains.Count);
        var parameters = new List<(string, string)>(domains.Count);
        for (var i = 0; i < domains.Count; i++)
        {
            var name = $"@sbDomain{i}";
            placeholders.Add(name);
            parameters.Add((name, domains[i]));
        }
        return ($" AND {prefix}domain IN ({string.Join(", ", placeholders)})", parameters);
    }

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
