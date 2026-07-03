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
    private readonly ILogger<SqliteDashboardEventStore> _logger;
    private readonly TimeSpan _detectionRetention;
    private readonly double _botFloor;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _initialized;

    public SqliteDashboardEventStore(
        ILogger<SqliteDashboardEventStore> logger,
        IOptions<BotDetectionOptions> options,
        IOptions<Configuration.StyloBotDashboardOptions>? dashboardOptions = null)
    {
        _logger = logger;
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
                ("detections", "is_verified_bot", "INTEGER DEFAULT 0")
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
            // (default 7 days; configurable per host).
            await using var pruneCmd = conn.CreateCommand();
            pruneCmd.CommandText = "DELETE FROM detections WHERE timestamp < @cutoff";
            pruneCmd.Parameters.AddWithValue("@cutoff",
                DateTime.UtcNow.Subtract(_detectionRetention).ToString("O"));
            var pruned = await pruneCmd.ExecuteNonQueryAsync(ct);
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
                    is_verified_bot)
                VALUES (@ts, @sig, @method, @path, @isBot, @prob, @conf, @risk, @name, @type, @action, @country, @ms,
                    @threat, @band, @status, @uaRaw, @justification, @domain, @host, @refHost, @deviceClass, @responseBytes,
                    @verifiedBot)
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
        var sql = "SELECT d.*, s.top_reasons_json AS top_reasons_json FROM detections d LEFT JOIN signatures s ON d.signature = s.signature";
        var conditions = new List<string>();
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

        // Audience predicate for the detection-level sub-query only.
        var audiencePredicate = audienceFilter?.ToLowerInvariant() switch
        {
            "bots"   => " AND bot_probability >= @botFloor",
            "humans" => " AND bot_probability < @botFloor",
            _        => string.Empty
        };
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);

        // Request-level counts (one detection row per request — drives traffic charts).
        // Also aggregates bytes_out, avg/max processing time for the KPI strip.
        int total = 0, bots = 0;
        long bytesOut = 0;
        double avgMs = 0.0, maxMs = 0.0;

        await using (var cmd = conn.CreateCommand())
        {
            var untilClause = hasUntil ? " AND timestamp < @until" : string.Empty;
            cmd.CommandText = $"""
                SELECT
                    COUNT(*) AS total,
                    SUM(CASE WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END) AS bots,
                    COALESCE(SUM(response_bytes), 0) AS bytes_out,
                    AVG(processing_time_ms) AS avg_ms,
                    MAX(processing_time_ms) AS max_ms
                FROM detections
                WHERE timestamp >= @since{untilClause}{audiencePredicate}{domainPredicate}
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

        // Audience predicate restricts each bucket to the matching audience.
        var audiencePredicate = audienceFilter?.ToLowerInvariant() switch
        {
            "bots"   => " AND bot_probability >= @botFloor",
            "humans" => " AND bot_probability < @botFloor",
            _        => string.Empty
        };
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT
                strftime('%Y-%m-%dT%H:%M:%SZ',
                         (CAST(strftime('%s', timestamp) AS INTEGER) / @bucket) * @bucket,
                         'unixepoch') AS bucket,
                SUM(CASE WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END) AS bots,
                SUM(CASE WHEN bot_probability < @botFloor THEN 1 ELSE 0 END) AS humans,
                COUNT(*) AS total,
                COALESCE(SUM(response_bytes), 0) AS bytes_out,
                AVG(processing_time_ms) AS avg_ms,
                MAX(processing_time_ms) AS max_ms
            FROM detections
            WHERE timestamp >= @start AND timestamp < @end{audiencePredicate}{domainPredicate}
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

    // NOTE: This query joins the 'sessions' table created by SqliteSessionStore (core package).
    // Both stores share the same database file. The join is intentional; on a fresh DB with no
    // sessions yet, the subquery returns NULL for last_path, which is handled via reader.IsDBNull(12).
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
        var isBotPredicate = audienceFilter?.ToLowerInvariant() switch
        {
            "all"    => string.Empty,
            "humans" => "WHERE s.bot_probability < @botFloor",
            _        => "WHERE s.bot_probability >= @botFloor"
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

        await using var cmd = conn.CreateCommand();
        // bytes_out is computed over ALL detections for this signature (no time filter).
        // This is the all-time / cache-seed path — windowed calls go to GetTopBotsWindowedAsync.
        cmd.CommandText = $"""
            SELECT s.signature, s.bot_name, s.bot_type, s.bot_probability, s.hit_count, s.last_seen,
                   s.threat_score, s.threat_band, s.action, s.narrative, s.top_reasons_json, s.country_code,
                   (SELECT json_extract(ses.paths_json, '$[0]')
                    FROM sessions ses
                    WHERE ses.signature = s.signature AND ses.paths_json IS NOT NULL
                    ORDER BY ses.ended_at DESC
                    LIMIT 1) AS last_path,
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

        // Audience predicate (mirrors GetTopBotsAsync): null/"bots" -> is_bot=1 (legacy),
        // "humans" -> is_bot=0, "all" -> no predicate.
        var audiencePredicate = audienceFilter?.ToLowerInvariant() switch
        {
            "all"    => string.Empty,
            "humans" => " AND bot_probability < @botFloor",
            _        => " AND bot_probability >= @botFloor"
        };
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
                   COUNT(*)              AS hit_count,
                   MAX(timestamp)        AS last_seen,
                   AVG(threat_score)     AS threat_score,
                   action,
                   threat_band,
                   country_code,
                   COALESCE(SUM(response_bytes), 0) AS bytes_out,
                   is_bot,
                   user_agent_raw
            FROM detections
            WHERE 1=1{audiencePredicate}{timeWhere}{domainPredicate}
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
                IsKnownBot       = reader.GetInt32(11) == 1,
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
    public async Task<List<DashboardCountryStats>> GetCountryStatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null, string? audienceFilter = null, IReadOnlyList<string>? domains = null)
    {
        await EnsureInitializedAsync();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();

        // Build WHERE clause — mirrors Task-4 GetEndpointStatsAsync convention.
        var where = new System.Text.StringBuilder("WHERE country_code IS NOT NULL AND country_code != ''");
        if (startTime.HasValue) where.Append(" AND timestamp >= @start");
        if (endTime.HasValue)   where.Append(" AND timestamp <= @end");
        switch (audienceFilter?.ToLowerInvariant())
        {
            case "humans": where.Append(" AND bot_probability < @botFloor"); break;
            case "bots":   where.Append(" AND bot_probability >= @botFloor"); break;
        }
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);
        if (domainPredicate.Length > 0) where.Append(domainPredicate);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"""
            SELECT country_code,
                   COUNT(*) as total,
                   SUM(CASE WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END) as bots,
                   AVG(processing_time_ms) AS avg_ms,
                   MAX(processing_time_ms) AS max_ms,
                   COALESCE(SUM(response_bytes), 0) as bytes_out
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
                COUNT(*) AS total,
                SUM(CASE WHEN bot_probability >= @botFloor THEN 1 ELSE 0 END) AS bots
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
        // to the is_bot column written by AddDetectionAsync (detection.IsBot ? 1 : 0).
        // "honeypot" is a path-shape filter applied post-query because IsHoneypot is derived
        // from HoneypotPathDefinitions.Classify, not a column on detections.
        var honeypotOnly = string.Equals(audienceFilter, "honeypot", StringComparison.OrdinalIgnoreCase);
        var where = new System.Text.StringBuilder("WHERE 1=1");
        if (startTime.HasValue) where.Append(" AND timestamp >= @start");
        if (endTime.HasValue)   where.Append(" AND timestamp <= @end");
        switch (audienceFilter?.ToLowerInvariant())
        {
            case "humans": where.Append(" AND bot_probability < @botFloor"); break;
            case "bots":   where.Append(" AND bot_probability >= @botFloor"); break;
            // "honeypot" filters in-memory after classification; no additional SQL predicate.
            // null / "all" / anything else: no additional predicate
        }
        var (domainPredicate, domainParams) = BuildDomainPredicate(domains, columnAlias: string.Empty);
        if (domainPredicate.Length > 0) where.Append(domainPredicate);

        await using var cmd = conn.CreateCommand();
        // SQLite lacks PERCENTILE_CONT, so p95 is approximated using avg + 90% of (max - avg).
        // Crude but consistent with the existing convention; the Postgres backend returns true p95.
        // Column order (0-based): method(0), path(1), total(2), bots(3), sigs(4),
        //   avg_ms(5), min_ms(6), max_ms(7), avg_threat(8), last_seen(9), bytes_out(10),
        //   s2xx(11), s3xx(12), s4xx(13), s5xx(14)
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
                   SUM(CASE WHEN status_code BETWEEN 500 AND 599 THEN 1 ELSE 0 END) as s5xx
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
        foreach (var (name, value) in domainParams) cmd.Parameters.AddWithValue(name, value);

        var results = new List<DashboardEndpointStats>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var total = reader.GetInt32(2);
            var bots  = reader.GetInt32(3);
            var avgMs = reader.GetDouble(5);
            var minMs = reader.IsDBNull(6) ? 0 : reader.GetDouble(6);
            var maxMs = reader.IsDBNull(7) ? 0 : reader.GetDouble(7);
            // p95 approximation: avg + 90% of the gap to max. Matches the Postgres
            // backend convention; real percentile requires PERCENTILE_CONT (Task 10).
            var p95Ms = avgMs + (maxMs - avgMs) * 0.9;
            var path = reader.GetString(1);
            // IsHoneypot is derived per-row from the static HoneypotPathDefinitions
            // classifier rather than stored on dashboard_events. Cheap (substring
            // + dictionary lookup); keeps schema migrations out of the dashboard.
            // The view's badge + the new "honeypot" audience filter both read this.
            var honeypotTier = Mostlylucid.BotDetection.Honeypot.HoneypotPathDefinitions
                .Classify(path, out _);
            var isHoneypot = honeypotTier > Mostlylucid.BotDetection.Honeypot.HoneypotTier.None;
            if (honeypotOnly && !isHoneypot) continue;
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

    public async Task<List<ThreatEntry>> GetThreatsAsync(int count = 20, DateTime? startTime = null, DateTime? endTime = null)
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
            {baseSql}
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
            {baseSql}
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
        ctryCmd.CommandText = $"""
            SELECT
                d.country_code,
                COUNT(*) AS Count,
                SUM(CASE WHEN d.bot_probability >= @botFloor THEN 1 ELSE 0 END) AS BotCount
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

    /// <summary>
    ///     Distinct <c>dashboard_detections.domain</c> values seen in the last
    ///     <paramref name="lookbackDays"/> days, ordered by detection count DESC.
    ///     Backs the multi-select domain picker on the Traffic page.
    /// </summary>
    public async Task<IReadOnlyList<Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic.DomainOption>> GetDomainOptionsAsync(
        int lookbackDays = 30,
        int limit = 100,
        CancellationToken ct = default)
    {
        await EnsureInitializedAsync(ct);
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync(ct);

        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT domain, COUNT(*) AS n
            FROM detections
            WHERE domain IS NOT NULL AND domain != ''
              AND timestamp >= @since
            GROUP BY domain
            ORDER BY n DESC
            LIMIT @limit
            """;
        cmd.Parameters.AddWithValue("@since", DateTime.UtcNow.AddDays(-Math.Max(1, lookbackDays)).ToString("O"));
        cmd.Parameters.AddWithValue("@limit", Math.Max(1, limit));

        var results = new List<Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic.DomainOption>(limit);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var value = reader.GetString(0);
            var count = reader.IsDBNull(1) ? 0L : Convert.ToInt64(reader.GetValue(1));
            var (label, isInternal) = FormatDomainLabel(value);
            results.Add(new Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic.DomainOption(
                Value: value, DisplayLabel: label, Count: count, IsInternal: isInternal));
        }
        return results;
    }

    /// <summary>
    ///     UI-only label transform: hosts under the internal cluster gateway
    ///     render as "internal" so operators aren't staring at
    ///     <c>stylobot-gateway.stylobot-system.svc.cluster.local</c>. The raw
    ///     value stays intact for URL + SQL round-trip.
    /// </summary>
    private static (string Label, bool IsInternal) FormatDomainLabel(string value)
    {
        if (value.StartsWith("stylobot-gateway.", StringComparison.OrdinalIgnoreCase))
            return ("internal", true);
        return (value, false);
    }

    public ValueTask DisposeAsync()
    {
        _initLock.Dispose();
        _writeLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
