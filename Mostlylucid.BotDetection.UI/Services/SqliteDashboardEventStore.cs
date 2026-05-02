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
                ("signatures", "risk_justification", "TEXT")
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

    public async Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        var sql = "SELECT * FROM detections";
        var conditions = new List<string>();
        var cmd = conn.CreateCommand();

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
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            results.Add(new DashboardDetectionEvent
            {
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                PrimarySignature = reader.GetString(reader.GetOrdinal("signature")),
                RequestId = reader.GetString(reader.GetOrdinal("signature")),
                Method = reader.IsDBNull(reader.GetOrdinal("method")) ? "" : reader.GetString(reader.GetOrdinal("method")),
                Path = reader.IsDBNull(reader.GetOrdinal("path")) ? "/" : reader.GetString(reader.GetOrdinal("path")),
                IsBot = reader.GetInt32(reader.GetOrdinal("is_bot")) == 1,
                BotProbability = reader.GetDouble(reader.GetOrdinal("bot_probability")),
                Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
                RiskBand = reader.IsDBNull(reader.GetOrdinal("risk_band")) ? "Unknown" : reader.GetString(reader.GetOrdinal("risk_band")),
                BotName = reader.IsDBNull(reader.GetOrdinal("bot_name")) ? null : reader.GetString(reader.GetOrdinal("bot_name")),
                BotType = reader.IsDBNull(reader.GetOrdinal("bot_type")) ? null : reader.GetString(reader.GetOrdinal("bot_type")),
                Action = reader.IsDBNull(reader.GetOrdinal("action")) ? null : reader.GetString(reader.GetOrdinal("action")),
                CountryCode = reader.IsDBNull(reader.GetOrdinal("country_code")) ? null : reader.GetString(reader.GetOrdinal("country_code")),
                ProcessingTimeMs = reader.GetDouble(reader.GetOrdinal("processing_time_ms")),
                ThreatScore = reader.GetDouble(reader.GetOrdinal("threat_score")),
                ThreatBand = reader.IsDBNull(reader.GetOrdinal("threat_band")) ? null : reader.GetString(reader.GetOrdinal("threat_band")),
                StatusCode = reader.GetInt32(reader.GetOrdinal("status_code")),
                UserAgentRaw = SafeGetString(reader, "user_agent_raw"),
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
        if (isBot.HasValue) sql += " WHERE is_bot = " + (isBot.Value ? "1" : "0");
        sql += " ORDER BY last_seen DESC LIMIT @limit OFFSET @offset";

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
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

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT
                COUNT(*) as total,
                SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots,
                COUNT(DISTINCT signature) as signatures
            FROM detections
            WHERE timestamp >= @since
            """;
        cmd.Parameters.AddWithValue("@since", DateTime.UtcNow.AddHours(-6).ToString("O"));

        await using var reader = await cmd.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            var total = reader.IsDBNull(0) ? 0 : reader.GetInt32(0);
            var bots = reader.IsDBNull(1) ? 0 : reader.GetInt32(1);
            var sigs = reader.IsDBNull(2) ? 0 : reader.GetInt32(2);
            return new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = total,
                BotRequests = bots,
                HumanRequests = total - bots,
                UncertainRequests = 0,
                UniqueSignatures = sigs,
                RiskBandCounts = new Dictionary<string, int>(),
                TopBotTypes = new Dictionary<string, int>(),
                TopActions = new Dictionary<string, int>()
            };
        }

        return new DashboardSummary
        {
            Timestamp = DateTime.UtcNow,
            TotalRequests = 0, BotRequests = 0, HumanRequests = 0, UncertainRequests = 0,
            UniqueSignatures = 0, RiskBandCounts = new(), TopBotTypes = new(), TopActions = new()
        };
    }

    public async Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(DateTime startTime, DateTime endTime, TimeSpan bucketSize)
    {
        await EnsureInitializedAsync();
        var points = new List<DashboardTimeSeriesPoint>();
        var current = startTime;

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        while (current < endTime)
        {
            var bucketEnd = current.Add(bucketSize);

            await using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                SELECT
                    SUM(CASE WHEN is_bot = 1 THEN 1 ELSE 0 END) as bots,
                    SUM(CASE WHEN is_bot = 0 THEN 1 ELSE 0 END) as humans,
                    COUNT(*) as total
                FROM detections
                WHERE timestamp >= @start AND timestamp < @end
                """;
            cmd.Parameters.AddWithValue("@start", current.ToString("O"));
            cmd.Parameters.AddWithValue("@end", bucketEnd.ToString("O"));

            await using var reader = await cmd.ExecuteReaderAsync();
            if (await reader.ReadAsync())
            {
                points.Add(new DashboardTimeSeriesPoint
                {
                    Timestamp = current,
                    BotCount = reader.IsDBNull(0) ? 0 : reader.GetInt32(0),
                    HumanCount = reader.IsDBNull(1) ? 0 : reader.GetInt32(1),
                    TotalCount = reader.IsDBNull(2) ? 0 : reader.GetInt32(2)
                });
            }

            current = bucketEnd;
        }

        return points;
    }

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
        var stats = await GetCountryStatsAsync(1000);
        var country = stats.FirstOrDefault(c => c.CountryCode == countryCode);
        if (country == null) return null;

        return new DashboardCountryDetail
        {
            CountryCode = country.CountryCode,
            TotalCount = country.TotalCount,
            BotCount = country.BotCount,
            BotRate = country.BotRate,
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
        var stats = await GetEndpointStatsAsync(1000);
        var endpoint = stats.FirstOrDefault(e => e.Method == method && e.Path == path);
        if (endpoint == null) return null;

        return new DashboardEndpointDetail
        {
            Method = method,
            Path = path,
            TotalCount = endpoint.TotalCount,
            BotCount = endpoint.BotCount,
            BotRate = endpoint.BotRate,
            UniqueSignatures = endpoint.UniqueSignatures,
            AvgProcessingTimeMs = endpoint.AvgProcessingTimeMs,
            AvgThreatScore = endpoint.AvgThreatScore,
            TopActions = new Dictionary<string, int>(),
            TopCountries = new Dictionary<string, int>(),
            RiskBands = new Dictionary<string, int>(),
            TopBots = new List<DashboardTopBotEntry>(),
            RecentDetections = new List<SignatureDetectionRow>()
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
        while (await reader.ReadAsync())
        {
            var path = reader.IsDBNull(reader.GetOrdinal("path")) ? "/" : reader.GetString(reader.GetOrdinal("path"));
            var action = reader.IsDBNull(reader.GetOrdinal("action")) ? null : reader.GetString(reader.GetOrdinal("action"));
            var threatScore = reader.IsDBNull(reader.GetOrdinal("threat_score")) ? 0 : reader.GetDouble(reader.GetOrdinal("threat_score"));

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
                Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("timestamp"))),
                Signature = reader.GetString(reader.GetOrdinal("signature")),
                Path = path,
                BotName = reader.IsDBNull(reader.GetOrdinal("bot_name")) ? null : reader.GetString(reader.GetOrdinal("bot_name")),
                BotType = reader.IsDBNull(reader.GetOrdinal("bot_type")) ? null : reader.GetString(reader.GetOrdinal("bot_type")),
                BotProbability = reader.GetDouble(reader.GetOrdinal("bot_probability")),
                ThreatScore = threatScore,
                ThreatBand = reader.IsDBNull(reader.GetOrdinal("threat_band")) ? null : reader.GetString(reader.GetOrdinal("threat_band")),
                CountryCode = reader.IsDBNull(reader.GetOrdinal("country_code")) ? null : reader.GetString(reader.GetOrdinal("country_code")),
                CveId = cveId,
                CveSeverity = cveSeverity,
                PackId = packId,
                InHoneypot = inHoneypot
            });
        }

        return results;
    }

    private static DashboardSignatureEvent ReadSignature(SqliteDataReader reader)
    {
        var sig = reader.GetString(reader.GetOrdinal("signature"));
        return new DashboardSignatureEvent
        {
            SignatureId = sig,
            PrimarySignature = sig,
            Timestamp = DateTime.Parse(reader.GetString(reader.GetOrdinal("last_seen"))),
            RiskBand = reader.IsDBNull(reader.GetOrdinal("risk_band")) ? "Unknown" : reader.GetString(reader.GetOrdinal("risk_band")),
            BotName = reader.IsDBNull(reader.GetOrdinal("bot_name")) ? null : reader.GetString(reader.GetOrdinal("bot_name")),
            BotType = reader.IsDBNull(reader.GetOrdinal("bot_type")) ? null : reader.GetString(reader.GetOrdinal("bot_type")),
            IsKnownBot = reader.GetInt32(reader.GetOrdinal("is_bot")) == 1,
            BotProbability = reader.GetDouble(reader.GetOrdinal("bot_probability")),
            Confidence = reader.GetDouble(reader.GetOrdinal("confidence")),
            Action = reader.IsDBNull(reader.GetOrdinal("action")) ? null : reader.GetString(reader.GetOrdinal("action")),
            HitCount = reader.GetInt32(reader.GetOrdinal("hit_count")),
            ProcessingTimeMs = reader.GetDouble(reader.GetOrdinal("processing_time_ms")),
            ThreatScore = reader.IsDBNull(reader.GetOrdinal("threat_score")) ? 0 : reader.GetDouble(reader.GetOrdinal("threat_score")),
            ThreatBand = reader.IsDBNull(reader.GetOrdinal("threat_band")) ? null : reader.GetString(reader.GetOrdinal("threat_band")),
            Narrative = reader.IsDBNull(reader.GetOrdinal("narrative")) ? null : reader.GetString(reader.GetOrdinal("narrative")),
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

    public ValueTask DisposeAsync()
    {
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
