using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Privacy;
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
    private readonly ILogger<SqliteDashboardEventStore> _logger;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteDashboardEventStore(
        ILogger<SqliteDashboardEventStore> logger,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _connectionString = DashboardDbPath.GetConnectionString(options.Value);
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

            const string schemaSql = """
            PRAGMA journal_mode=WAL;
            PRAGMA synchronous=NORMAL;
            PRAGMA cache_size=-4000;

            CREATE TABLE IF NOT EXISTS detections (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                timestamp TEXT NOT NULL,
                signature TEXT NOT NULL,
                method TEXT,
                path TEXT,
                is_bot INTEGER NOT NULL,
                bot_probability REAL NOT NULL,
                confidence REAL NOT NULL,
                risk_band TEXT,
                bot_name TEXT,
                bot_type TEXT,
                action TEXT,
                country_code TEXT,
                processing_time_ms REAL,
                threat_score REAL DEFAULT 0,
                threat_band TEXT,
                status_code INTEGER DEFAULT 0,
                user_agent_raw TEXT,
                risk_justification TEXT
            );

            CREATE TABLE IF NOT EXISTS signatures (
                signature TEXT PRIMARY KEY,
                bot_name TEXT,
                bot_type TEXT,
                is_bot INTEGER NOT NULL DEFAULT 0,
                bot_probability REAL NOT NULL DEFAULT 0,
                confidence REAL NOT NULL DEFAULT 0,
                risk_band TEXT,
                action TEXT,
                country_code TEXT,
                hit_count INTEGER NOT NULL DEFAULT 1,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                processing_time_ms REAL DEFAULT 0,
                threat_score REAL DEFAULT 0,
                threat_band TEXT,
                narrative TEXT,
                metadata_json TEXT,
                risk_justification TEXT
            );

            CREATE INDEX IF NOT EXISTS idx_det_timestamp ON detections(timestamp DESC);
            CREATE INDEX IF NOT EXISTS idx_det_signature ON detections(signature);
            CREATE INDEX IF NOT EXISTS idx_det_is_bot ON detections(is_bot);
            CREATE INDEX IF NOT EXISTS idx_det_country ON detections(country_code);
            CREATE INDEX IF NOT EXISTS idx_det_path ON detections(path);
            CREATE INDEX IF NOT EXISTS idx_sig_last_seen ON signatures(last_seen DESC);
            CREATE INDEX IF NOT EXISTS idx_sig_is_bot ON signatures(is_bot);
            CREATE INDEX IF NOT EXISTS idx_det_threat ON detections(threat_score DESC, timestamp DESC);

            CREATE TABLE IF NOT EXISTS user_agent_stats (
                ua_family TEXT NOT NULL,
                ua_version TEXT NOT NULL DEFAULT '',
                ua_os TEXT NOT NULL DEFAULT '',
                is_bot INTEGER NOT NULL DEFAULT 0,
                first_seen TEXT NOT NULL,
                last_seen TEXT NOT NULL,
                hit_count INTEGER NOT NULL DEFAULT 1,
                unique_signatures INTEGER NOT NULL DEFAULT 1,
                PRIMARY KEY (ua_family, ua_version, ua_os)
            );
            CREATE INDEX IF NOT EXISTS idx_ua_family ON user_agent_stats(ua_family, hit_count DESC);

            CREATE TABLE IF NOT EXISTS metric_snapshots (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                bucket_time TEXT    NOT NULL,
                pack_id     TEXT    NOT NULL,
                meter_name  TEXT    NOT NULL,
                instrument  TEXT    NOT NULL,
                tags        TEXT,
                value       REAL    NOT NULL,
                value_type  TEXT    NOT NULL
            );
            CREATE INDEX IF NOT EXISTS idx_ms_lookup
                ON metric_snapshots(bucket_time, pack_id, instrument);
            """;
            foreach (var statement in schemaSql.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                try
                {
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = statement;
                    await cmd.ExecuteNonQueryAsync(ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to initialize SQLite dashboard schema statement: {statement}",
                        ex);
                }
            }

            // Migrate: add risk_justification column if absent (idempotent).
            // SQLite does not support IF NOT EXISTS on ALTER TABLE ADD COLUMN;
            // use PRAGMA table_info to check first.
            foreach (var (table, column, colDef) in new (string, string, string)[]
            {
                ("detections", "user_agent_raw", "TEXT"),
                ("detections", "risk_justification", "TEXT"),
                ("signatures", "risk_justification", "TEXT"),
                ("signatures", "top_reasons_json", "TEXT")
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

            // Prune old detections (keep last 7 days)
            await using var pruneCmd = conn.CreateCommand();
            pruneCmd.CommandText = "DELETE FROM detections WHERE timestamp < @cutoff";
            pruneCmd.Parameters.AddWithValue("@cutoff", DateTime.UtcNow.AddDays(-7).ToString("O"));
            var pruned = await pruneCmd.ExecuteNonQueryAsync(ct);
            if (pruned > 0) _logger.LogDebug("Pruned {Count} old dashboard detections", pruned);

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
                    status_code, user_agent_raw, risk_justification)
                VALUES (@ts, @sig, @method, @path, @isBot, @prob, @conf, @risk, @name, @type, @action, @country, @ms,
                    @threat, @band, @status, @uaRaw, @justification)
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
            await cmd.ExecuteNonQueryAsync();

            // Upsert UA stats for analytics
            await UpsertUserAgentStatsAsync(conn, strippedUa, detection.IsBot);
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
            cmd.CommandText = """
                INSERT INTO signatures (signature, bot_name, bot_type, is_bot, bot_probability, confidence,
                    risk_band, action, country_code, hit_count, first_seen, last_seen, processing_time_ms,
                    threat_score, threat_band, narrative, risk_justification)
                VALUES (@sig, @name, @type, @isBot, @prob, @conf, @risk, @action, @country, 1, @now, @now, @ms,
                    @threat, @band, @narrative, @justification)
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
                    risk_justification = COALESCE(@justification, risk_justification)
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
            cmd.Parameters.AddWithValue("@now", DateTime.UtcNow.ToString("O"));

            var hitCount = await cmd.ExecuteScalarAsync();
            return signature with { HitCount = Convert.ToInt32(hitCount ?? 1) };
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

        var sql = "SELECT * FROM detections";
        var conditions = new List<string>();
        await using var cmd = conn.CreateCommand();

        if (filter?.StartTime.HasValue == true)
        {
            conditions.Add("timestamp >= @start");
            cmd.Parameters.AddWithValue("@start", filter.StartTime.Value.ToString("O"));
        }
        if (filter?.EndTime.HasValue == true)
        {
            conditions.Add("timestamp <= @end");
            cmd.Parameters.AddWithValue("@end", filter.EndTime.Value.ToString("O"));
        }
        if (filter?.IsBot.HasValue == true)
        {
            conditions.Add("is_bot = @isBot");
            cmd.Parameters.AddWithValue("@isBot", filter.IsBot.Value ? 1 : 0);
        }
        if (!string.IsNullOrEmpty(filter?.SignatureId))
        {
            conditions.Add("signature = @sig");
            cmd.Parameters.AddWithValue("@sig", filter.SignatureId);
        }

        if (conditions.Count > 0)
            sql += " WHERE " + string.Join(" AND ", conditions);

        sql += " ORDER BY timestamp DESC LIMIT @limit";
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
                RiskJustification = SafeGetString(reader, "risk_justification")
            });
        }
        return results;
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

    public async Task<DashboardSummary> GetSummaryAsync()
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var since = DateTime.UtcNow.AddHours(-6).ToString("O");

        // Request-level counts (one detection row per request — drives traffic charts).
        int total = 0, bots = 0;
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COUNT(*) AS total,
                    SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) AS bots
                FROM detections
                WHERE timestamp >= @since
                """;
            cmd.Parameters.AddWithValue("@since", since);
            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
                bots  = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            }
        }

        // Fingerprint-level counts (one signatures row per unique fingerprint).
        // bot_probability is the EWMA-blended posterior, risk_band is the latest
        // band — so these are the "how many actors did we see" counts the
        // dashboard banner should be showing.
        int sigs = 0, botSigs = 0, humanSigs = 0, highSigs = 0;
        var riskBands = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = """
                SELECT
                    COUNT(*) AS sigs,
                    SUM(CASE WHEN bot_probability >= 0.5 THEN 1 ELSE 0 END) AS bot_sigs,
                    SUM(CASE WHEN bot_probability < 0.5 THEN 1 ELSE 0 END) AS human_sigs,
                    SUM(CASE WHEN risk_band IN ('High','VeryHigh') THEN 1 ELSE 0 END) AS high_sigs
                FROM signatures
                WHERE last_seen >= @since
                """;
            cmd.Parameters.AddWithValue("@since", since);
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
            cmd.Parameters.AddWithValue("@since", since);
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
            TopActions = new Dictionary<string, int>()
        };
    }

    public async Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize)
    {
        await EnsureInitializedAsync();

        // Determine SQLite strftime format and gap-fill key format from bucket size
        string bucketFormat;
        int bucketSeconds;
        string keyFormat;
        if (bucketSize >= TimeSpan.FromDays(1))
        {
            bucketFormat = "%Y-%m-%dT00:00:00";
            bucketSeconds = (int)TimeSpan.FromDays(1).TotalSeconds;
            keyFormat = "yyyy-MM-ddT00:00:00";
        }
        else if (bucketSize >= TimeSpan.FromHours(1))
        {
            bucketFormat = "%Y-%m-%dT%H:00:00";
            bucketSeconds = (int)TimeSpan.FromHours(1).TotalSeconds;
            keyFormat = "yyyy-MM-ddTHH:00:00";
        }
        else
        {
            bucketFormat = "%Y-%m-%dT%H:%M:00";
            bucketSeconds = (int)bucketSize.TotalSeconds;
            keyFormat = "yyyy-MM-ddTHH:mm:00";
        }

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                strftime('{bucketFormat}', timestamp) AS bucket,
                SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) AS bots,
                SUM(CASE WHEN is_bot = 0 THEN 1 ELSE 0 END) AS humans,
                COUNT(*) AS total
            FROM detections
            WHERE timestamp >= @start AND timestamp < @end
            GROUP BY bucket
            ORDER BY bucket
            """;
        cmd.Parameters.AddWithValue("@start", startTime.ToString("O"));
        cmd.Parameters.AddWithValue("@end", endTime.ToString("O"));

        var dbPoints = new Dictionary<string, DashboardTimeSeriesPoint>(StringComparer.Ordinal);
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var bucket = reader.GetString(0);
            if (DateTime.TryParse(bucket, out var ts))
                dbPoints[bucket] = new DashboardTimeSeriesPoint
                {
                    Timestamp = ts,
                    BotCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    HumanCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2),
                    TotalCount = reader.IsDBNull(3) ? 0 : reader.GetInt32(3)
                };
        }

        // Fill gaps with zero-count buckets to keep chart series continuous
        var points = new List<DashboardTimeSeriesPoint>();
        var current = startTime;
        while (current < endTime)
        {
            var key = current.ToString(keyFormat);
            points.Add(dbPoints.TryGetValue(key, out var p) ? p : new DashboardTimeSeriesPoint
            {
                Timestamp = current,
                BotCount = 0,
                HumanCount = 0,
                TotalCount = 0
            });
            current = current.Add(bucketSize);
        }
        return points;
    }

    // NOTE: This query joins the 'sessions' table created by SqliteSessionStore (core package).
    // Both stores share the same database file. The join is intentional; on a fresh DB with no
    // sessions yet, the subquery returns NULL for last_path, which is handled via reader.IsDBNull(12).
    // top_reasons_json is migrated by EnsureInitializedAsync — absent on pre-migration DBs it reads NULL.
    public async Task<List<DashboardTopBotEntry>> GetTopBotsAsync(int count = 10, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT s.signature, s.bot_name, s.bot_type, s.bot_probability, s.hit_count, s.last_seen,
                   s.threat_score, s.threat_band, s.action, s.narrative, s.top_reasons_json, s.country_code,
                   (SELECT json_extract(ses.paths_json, '$[0]')
                    FROM sessions ses
                    WHERE ses.signature = s.signature AND ses.paths_json IS NOT NULL AND ses.is_bot = 1
                    ORDER BY ses.ended_at DESC
                    LIMIT 1) AS last_path
            FROM signatures s
            WHERE s.is_bot = 1
            ORDER BY s.hit_count DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);

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
                LastPath = reader.IsDBNull(12) ? null : reader.GetString(12)
            });
        }
        return results;
    }

    public async Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT country_code,
                   COUNT(*) as total,
                   SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots
            FROM detections
            WHERE country_code IS NOT NULL AND country_code != ''
            GROUP BY country_code
            ORDER BY total DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);

        var results = new List<DashboardCountryStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(1);
            var bots = reader.GetInt32(2);
            results.Add(new DashboardCountryStats
            {
                CountryCode = reader.GetString(0),
                TotalCount = total,
                BotCount = bots,
                BotRate = total > 0 ? (double)bots / total : 0
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
                COUNT(*) AS total,
                SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) AS bots
            FROM detections
            WHERE country_code = @cc{timeFilter}
            """;
        cmd.Parameters.AddWithValue("@cc", countryCode);
        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
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

    public async Task<List<DashboardEndpointStats>> GetEndpointStatsAsync(int count = 50, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT method, path,
                   COUNT(*) as total,
                   SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots,
                   COUNT(DISTINCT signature) as sigs,
                   AVG(processing_time_ms) as avg_ms,
                   AVG(threat_score) as avg_threat,
                   MAX(timestamp) as last_seen
            FROM detections
            GROUP BY method, path
            ORDER BY total DESC
            LIMIT @count
            """;
        cmd.Parameters.AddWithValue("@count", count);

        var results = new List<DashboardEndpointStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(2);
            var bots = reader.GetInt32(3);
            results.Add(new DashboardEndpointStats
            {
                Method = reader.GetString(0),
                Path = reader.GetString(1),
                TotalCount = total,
                BotCount = bots,
                BotRate = total > 0 ? (double)bots / total : 0,
                UniqueSignatures = reader.GetInt32(4),
                AvgProcessingTimeMs = reader.GetDouble(5),
                AvgThreatScore = reader.IsDBNull(6) ? 0 : reader.GetDouble(6),
                LastSeen = DateTime.Parse(reader.GetString(7))
            });
        }
        return results;
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
                SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) AS bots,
                COUNT(DISTINCT signature) AS sigs,
                AVG(processing_time_ms) AS avg_ms,
                AVG(threat_score) AS avg_threat
            FROM detections
            WHERE method = @method AND path = @path{timeFilter}
            """;
        cmd.Parameters.AddWithValue("@method", method);
        cmd.Parameters.AddWithValue("@path", path);
        if (startTime.HasValue) cmd.Parameters.AddWithValue("@start", startTime.Value.ToString("O"));
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

    public async Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        var sql = """
            SELECT timestamp, signature, path, bot_name, bot_type, bot_probability,
                   threat_score, threat_band, country_code, action, status_code
            FROM detections
            WHERE (action = 'simulation-pack'
                   OR threat_band IN ('Critical', 'High'))
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
                InHoneypot     = inHoneypot
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

        var whereClause = filter.EntityType switch
        {
            "signature" => "d.signature = @Value",
            "country"   => "d.country_code = @Value",
            "path"      => "d.path LIKE @Value",
            "ua_family" => "d.user_agent_raw LIKE @Value || '%'",
            _           => "1=0"
        };

        var timeFilter = "";
        if (filter.Start.HasValue) timeFilter += " AND d.timestamp >= @Start";
        if (filter.End.HasValue)   timeFilter += " AND d.timestamp <= @End";

        var baseSql = $"FROM detections d WHERE {whereClause}{timeFilter}";
        var paramValue = filter.EntityType == "path" ? $"%{filter.EntityValue}%" : filter.EntityValue;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);
        await using var txn = await conn.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted, ct);

        // ── Summary ──────────────────────────────────────────────────────────
        await using var summaryCmd = conn.CreateCommand();
        summaryCmd.CommandText = $"""
            SELECT
                COUNT(*) AS TotalDetections,
                MIN(timestamp) AS FirstSeen,
                MAX(timestamp) AS LastSeen,
                SUM(CASE WHEN risk_band = 'high'   THEN 1 ELSE 0 END) AS HighRisk,
                SUM(CASE WHEN risk_band = 'medium' THEN 1 ELSE 0 END) AS MediumRisk,
                SUM(CASE WHEN risk_band = 'low'    THEN 1 ELSE 0 END) AS LowRisk
            {baseSql}
            """;
        summaryCmd.Parameters.AddWithValue("@Value", paramValue);
        if (filter.Start.HasValue) summaryCmd.Parameters.AddWithValue("@Start", filter.Start.Value.ToString("o"));
        if (filter.End.HasValue)   summaryCmd.Parameters.AddWithValue("@End",   filter.End.Value.ToString("o"));

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
                d.threat_score, d.threat_band
            {baseSql}
            ORDER BY d.timestamp DESC
            LIMIT @Limit OFFSET @Offset
            """;
        detCmd.Parameters.AddWithValue("@Value",  paramValue);
        detCmd.Parameters.AddWithValue("@Limit",  filter.Limit);
        detCmd.Parameters.AddWithValue("@Offset", filter.Offset);
        if (filter.Start.HasValue) detCmd.Parameters.AddWithValue("@Start", filter.Start.Value.ToString("o"));
        if (filter.End.HasValue)   detCmd.Parameters.AddWithValue("@End",   filter.End.Value.ToString("o"));

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
                    ThreatBand       = r.IsDBNull(16) ? null : r.GetString(16)
                });
            }
        }

        // ── Signatures (distinct within result set) ───────────────────────────
        await using var sigCmd = conn.CreateCommand();
        sigCmd.CommandText = $"""
            SELECT
                s.signature, s.hit_count, s.bot_name, s.bot_type,
                s.risk_band, s.is_bot, s.last_seen
            FROM signatures s
            WHERE s.signature IN (SELECT DISTINCT d.signature {baseSql})
            ORDER BY s.hit_count DESC
            LIMIT 50
            """;
        sigCmd.Parameters.AddWithValue("@Value", paramValue);
        if (filter.Start.HasValue) sigCmd.Parameters.AddWithValue("@Start", filter.Start.Value.ToString("o"));
        if (filter.End.HasValue)   sigCmd.Parameters.AddWithValue("@End",   filter.End.Value.ToString("o"));

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
            {baseSql}
            GROUP BY d.method, d.path
            ORDER BY Count DESC
            LIMIT 50
            """;
        epCmd.Parameters.AddWithValue("@Value", paramValue);
        if (filter.Start.HasValue) epCmd.Parameters.AddWithValue("@Start", filter.Start.Value.ToString("o"));
        if (filter.End.HasValue)   epCmd.Parameters.AddWithValue("@End",   filter.End.Value.ToString("o"));

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
        ctryCmd.CommandText = $"""
            SELECT
                d.country_code,
                COUNT(*) AS Count,
                SUM(CASE WHEN d.is_bot = 1 THEN 1 ELSE 0 END) AS BotCount
            {baseSql} AND d.country_code IS NOT NULL
            GROUP BY d.country_code
            ORDER BY Count DESC
            LIMIT 50
            """;
        ctryCmd.Parameters.AddWithValue("@Value", paramValue);
        if (filter.Start.HasValue) ctryCmd.Parameters.AddWithValue("@Start", filter.Start.Value.ToString("o"));
        if (filter.End.HasValue)   ctryCmd.Parameters.AddWithValue("@End",   filter.End.Value.ToString("o"));

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

    public async Task<int> PruneOldDetectionsAsync(DateTime cutoff, CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await _writeLock.WaitAsync(ct);
        try
        {
            await using var conn = new SqliteConnection(_connectionString);
            await conn.OpenAsync(ct);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "DELETE FROM detections WHERE timestamp < @cutoff";
            cmd.Parameters.AddWithValue("@cutoff", cutoff.ToString("O"));
            return await cmd.ExecuteNonQueryAsync(ct);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
