using Dapper;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Mostlylucid.BotDetection.UI.Services;
using Npgsql;
using System.Text.Json;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Storage;

/// <summary>
/// PostgreSQL implementation of dashboard event store using Dapper.
/// Stores detections and signatures with GIN-indexed fast lookups.
/// </summary>
public class PostgreSQLDashboardEventStore : IDashboardEventStore
{
    private readonly PostgreSQLStorageOptions _options;
    private readonly ILogger<PostgreSQLDashboardEventStore> _logger;

    // Circuit breaker: stop trying PostgreSQL for 60s after a connection failure
    private DateTime _circuitOpenUntil = DateTime.MinValue;
    private static readonly TimeSpan CircuitBreakDuration = TimeSpan.FromSeconds(60);

    public PostgreSQLDashboardEventStore(
        PostgreSQLStorageOptions options,
        ILogger<PostgreSQLDashboardEventStore> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
        {
            throw new ArgumentException("ConnectionString is required", nameof(options));
        }

        // Enable Dapper snake_case → PascalCase mapping for PostgreSQL columns
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    private bool IsCircuitOpen
    {
        get
        {
            if (_circuitOpenUntil <= DateTime.UtcNow) return false;
            return true;
        }
    }

    private void TripCircuit()
    {
        var newExpiry = DateTime.UtcNow + CircuitBreakDuration;
        if (_circuitOpenUntil < newExpiry)
        {
            _circuitOpenUntil = newExpiry;
            _logger.LogWarning("PostgreSQL circuit breaker tripped — skipping DB calls for {Seconds}s", CircuitBreakDuration.TotalSeconds);
        }
    }

    public async Task AddDetectionAsync(DashboardDetectionEvent detection)
    {
        if (IsCircuitOpen) return;

        const string sql = @"
            INSERT INTO dashboard_detections (
                request_id, timestamp, is_bot, bot_probability, confidence,
                risk_band, bot_type, bot_name, action, policy_name,
                method, path, status_code, processing_time_ms, ip_address,
                user_agent, top_reasons, primary_signature
            ) VALUES (
                @RequestId, @Timestamp, @IsBot, @BotProbability, @Confidence,
                @RiskBand, @BotType, @BotName, @Action, @PolicyName,
                @Method, @Path, @StatusCode, @ProcessingTimeMs, @IpAddress::inet,
                @UserAgent, @TopReasons::jsonb, @PrimarySignature
            )";

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            await connection.ExecuteAsync(sql, new
            {
                detection.RequestId,
                detection.Timestamp,
                detection.IsBot,
                detection.BotProbability,
                detection.Confidence,
                detection.RiskBand,
                detection.BotType,
                detection.BotName,
                detection.Action,
                detection.PolicyName,
                detection.Method,
                detection.Path,
                detection.StatusCode,
                detection.ProcessingTimeMs,
                detection.IpAddress,
                detection.UserAgent,
                TopReasons = JsonSerializer.Serialize(detection.TopReasons),
                detection.PrimarySignature
            }, commandTimeout: _options.CommandTimeoutSeconds);
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            TripCircuit();
            _logger.LogError(ex, "Failed to add detection to PostgreSQL: {RequestId}", detection.RequestId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add detection to PostgreSQL: {RequestId}", detection.RequestId);
            throw;
        }
    }

    public async Task<DashboardSignatureEvent> AddSignatureAsync(DashboardSignatureEvent signature)
    {
        if (IsCircuitOpen) return signature;

        const string sql = @"
            INSERT INTO dashboard_signatures (
                signature_id, timestamp, primary_signature, ip_signature,
                ua_signature, client_side_signature, factor_count, risk_band,
                hit_count, is_known_bot, bot_name, metadata
            ) VALUES (
                @SignatureId, @Timestamp, @PrimarySignature, @IpSignature,
                @UaSignature, @ClientSideSignature, @FactorCount, @RiskBand,
                @HitCount, @IsKnownBot, @BotName, @Metadata::jsonb
            )
            ON CONFLICT (primary_signature)
            DO UPDATE SET
                hit_count = dashboard_signatures.hit_count + 1,
                timestamp = EXCLUDED.timestamp,
                risk_band = EXCLUDED.risk_band,
                is_known_bot = EXCLUDED.is_known_bot,
                bot_name = EXCLUDED.bot_name,
                updated_at = NOW()
            RETURNING hit_count";

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            var hitCount = await connection.ExecuteScalarAsync<int>(sql, new
            {
                signature.SignatureId,
                signature.Timestamp,
                signature.PrimarySignature,
                signature.IpSignature,
                signature.UaSignature,
                signature.ClientSideSignature,
                signature.FactorCount,
                signature.RiskBand,
                signature.HitCount,
                signature.IsKnownBot,
                signature.BotName,
                Metadata = "{}" // Empty JSONB for extensibility
            }, commandTimeout: _options.CommandTimeoutSeconds);

            // Return updated signature with actual hit count from database
            return signature with { HitCount = hitCount };
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            TripCircuit();
            _logger.LogError(ex, "Failed to add signature to PostgreSQL: {SignatureId}", signature.SignatureId);
            return signature;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to add signature to PostgreSQL: {SignatureId}", signature.SignatureId);
            throw;
        }
    }

    public async Task<List<DashboardDetectionEvent>> GetDetectionsAsync(DashboardFilter? filter = null)
    {
        if (IsCircuitOpen) return [];

        var sql = "SELECT * FROM dashboard_detections WHERE 1=1";
        var parameters = new DynamicParameters();

        if (filter != null)
        {
            if (filter.StartTime.HasValue)
            {
                sql += " AND timestamp >= @StartTime";
                parameters.Add("StartTime", filter.StartTime.Value);
            }

            if (filter.EndTime.HasValue)
            {
                sql += " AND timestamp <= @EndTime";
                parameters.Add("EndTime", filter.EndTime.Value);
            }

            if (filter.RiskBands?.Any() == true)
            {
                sql += " AND risk_band = ANY(@RiskBands)";
                parameters.Add("RiskBands", filter.RiskBands.ToArray());
            }

            if (filter.IsBot.HasValue)
            {
                sql += " AND is_bot = @IsBot";
                parameters.Add("IsBot", filter.IsBot.Value);
            }

            if (!string.IsNullOrEmpty(filter.PathContains))
            {
                sql += " AND path ILIKE @PathContains";
                parameters.Add("PathContains", $"%{filter.PathContains}%");
            }

            if (!string.IsNullOrEmpty(filter.SignatureId))
            {
                sql += " AND primary_signature = @SignatureId";
                parameters.Add("SignatureId", filter.SignatureId);
            }

            if (filter.HighRiskOnly)
            {
                sql += " AND risk_band IN ('High', 'VeryHigh')";
            }
        }

        sql += " ORDER BY timestamp DESC";

        if (filter?.Limit.HasValue == true)
        {
            sql += " LIMIT @Limit";
            parameters.Add("Limit", Math.Min(filter.Limit.Value, _options.MaxDetectionsPerQuery));
        }
        else
        {
            sql += " LIMIT @MaxLimit";
            parameters.Add("MaxLimit", _options.MaxDetectionsPerQuery);
        }

        if (filter?.Offset.HasValue == true)
        {
            sql += " OFFSET @Offset";
            parameters.Add("Offset", filter.Offset.Value);
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            var results = await connection.QueryAsync<DetectionRow>(sql, parameters,
                commandTimeout: _options.CommandTimeoutSeconds);

            return results.Select(MapToDetectionEvent).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get detections from PostgreSQL");
            throw;
        }
    }

    public async Task<List<DashboardSignatureEvent>> GetSignaturesAsync(int limit = 100)
    {
        if (IsCircuitOpen) return [];

        const string sql = @"
            SELECT * FROM dashboard_signatures
            ORDER BY timestamp DESC
            LIMIT @Limit";

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            var results = await connection.QueryAsync<SignatureRow>(sql, new { Limit = limit },
                commandTimeout: _options.CommandTimeoutSeconds);

            return results.Select(MapToSignatureEvent).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get signatures from PostgreSQL");
            throw;
        }
    }

    public async Task<DashboardSummary> GetSummaryAsync()
    {
        if (IsCircuitOpen) return new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0,
                BotRequests = 0,
                HumanRequests = 0,
                UncertainRequests = 0,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new(),
                UniqueSignatures = 0
            };

        const string sql = @"
            WITH stats AS (
                SELECT
                    COUNT(*) as total_requests,
                    COUNT(*) FILTER (WHERE is_bot) as bot_requests,
                    COUNT(*) FILTER (WHERE NOT is_bot AND confidence >= 0.5) as human_requests,
                    COUNT(*) FILTER (WHERE NOT is_bot AND confidence < 0.5) as uncertain_requests,
                    AVG(processing_time_ms) as avg_processing_time
                FROM dashboard_detections
                WHERE timestamp > NOW() - INTERVAL '24 hours'
            ),
            risk_bands AS (
                SELECT
                    risk_band,
                    COUNT(*)::int as count
                FROM dashboard_detections
                WHERE timestamp > NOW() - INTERVAL '24 hours'
                GROUP BY risk_band
            ),
            bot_types AS (
                SELECT
                    bot_type,
                    COUNT(*)::int as count
                FROM dashboard_detections
                WHERE bot_type IS NOT NULL
                    AND timestamp > NOW() - INTERVAL '24 hours'
                GROUP BY bot_type
                ORDER BY count DESC
                LIMIT 5
            ),
            actions AS (
                SELECT
                    action,
                    COUNT(*)::int as count
                FROM dashboard_detections
                WHERE action IS NOT NULL
                    AND timestamp > NOW() - INTERVAL '24 hours'
                GROUP BY action
                ORDER BY count DESC
                LIMIT 5
            ),
            unique_sigs AS (
                SELECT COUNT(DISTINCT primary_signature)::int as count
                FROM dashboard_signatures
            )
            SELECT
                (SELECT total_requests::int FROM stats) as TotalRequests,
                (SELECT bot_requests::int FROM stats) as BotRequests,
                (SELECT human_requests::int FROM stats) as HumanRequests,
                (SELECT uncertain_requests::int FROM stats) as UncertainRequests,
                (SELECT avg_processing_time FROM stats) as AverageProcessingTimeMs,
                (SELECT count FROM unique_sigs) as UniqueSignatures";

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);

            var summary = await connection.QuerySingleAsync<SummaryRow>(sql,
                commandTimeout: _options.CommandTimeoutSeconds);

            // Get risk band counts
            var riskBands = await connection.QueryAsync<(string RiskBand, int Count)>(
                "SELECT risk_band, COUNT(*)::int as count FROM dashboard_detections WHERE timestamp > NOW() - INTERVAL '24 hours' GROUP BY risk_band",
                commandTimeout: _options.CommandTimeoutSeconds);

            // Get top bot types
            var botTypes = await connection.QueryAsync<(string BotType, int Count)>(
                "SELECT bot_type, COUNT(*)::int as count FROM dashboard_detections WHERE bot_type IS NOT NULL AND timestamp > NOW() - INTERVAL '24 hours' GROUP BY bot_type ORDER BY count DESC LIMIT 5",
                commandTimeout: _options.CommandTimeoutSeconds);

            // Get top actions
            var actions = await connection.QueryAsync<(string Action, int Count)>(
                "SELECT action, COUNT(*)::int as count FROM dashboard_detections WHERE action IS NOT NULL AND timestamp > NOW() - INTERVAL '24 hours' GROUP BY action ORDER BY count DESC LIMIT 5",
                commandTimeout: _options.CommandTimeoutSeconds);

            return new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = summary.TotalRequests,
                BotRequests = summary.BotRequests,
                HumanRequests = summary.HumanRequests,
                UncertainRequests = summary.UncertainRequests,
                RiskBandCounts = riskBands.ToDictionary(x => x.RiskBand, x => x.Count),
                TopBotTypes = botTypes.ToDictionary(x => x.BotType, x => x.Count),
                TopActions = actions.ToDictionary(x => x.Action, x => x.Count),
                AverageProcessingTimeMs = summary.AverageProcessingTimeMs,
                UniqueSignatures = summary.UniqueSignatures
            };
        }
        catch (Exception ex) when (ex is NpgsqlException or System.Net.Sockets.SocketException)
        {
            TripCircuit();
            _logger.LogError(ex, "Failed to get summary from PostgreSQL");
            return new DashboardSummary
            {
                Timestamp = DateTime.UtcNow,
                TotalRequests = 0,
                BotRequests = 0,
                HumanRequests = 0,
                UncertainRequests = 0,
                RiskBandCounts = new(),
                TopBotTypes = new(),
                TopActions = new(),
                UniqueSignatures = 0
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get summary from PostgreSQL");
            throw;
        }
    }

    public async Task<List<DashboardTimeSeriesPoint>> GetTimeSeriesAsync(
        DateTime startTime,
        DateTime endTime,
        TimeSpan bucketSize)
    {
        if (IsCircuitOpen) return [];

        // Use TimescaleDB time_bucket if enabled, otherwise use standard PostgreSQL date_trunc
        string sql;
        if (_options.EnableTimescaleDB)
        {
            sql = $@"
                WITH time_buckets AS (
                    SELECT
                        time_bucket('{(int)bucketSize.TotalSeconds} seconds', timestamp) as bucket,
                        COUNT(*) FILTER (WHERE is_bot)::int as bot_count,
                        COUNT(*) FILTER (WHERE NOT is_bot)::int as human_count,
                        COUNT(*)::int as total_count
                    FROM dashboard_detections
                    WHERE timestamp >= @StartTime AND timestamp <= @EndTime
                    GROUP BY bucket
                    ORDER BY bucket
                )
                SELECT
                    bucket as Timestamp,
                    bot_count as BotCount,
                    human_count as HumanCount,
                    total_count as TotalCount
                FROM time_buckets";
        }
        else
        {
            // Standard PostgreSQL fallback using date_trunc
            // Map bucket size to nearest supported interval
            var truncUnit = GetDateTruncUnit(bucketSize);
            sql = $@"
                WITH time_buckets AS (
                    SELECT
                        date_trunc('{truncUnit}', timestamp) as bucket,
                        COUNT(*) FILTER (WHERE is_bot)::int as bot_count,
                        COUNT(*) FILTER (WHERE NOT is_bot)::int as human_count,
                        COUNT(*)::int as total_count
                    FROM dashboard_detections
                    WHERE timestamp >= @StartTime AND timestamp <= @EndTime
                    GROUP BY bucket
                    ORDER BY bucket
                )
                SELECT
                    bucket as Timestamp,
                    bot_count as BotCount,
                    human_count as HumanCount,
                    total_count as TotalCount
                FROM time_buckets";
        }

        try
        {
            await using var connection = new NpgsqlConnection(_options.ConnectionString);
            var rows = await connection.QueryAsync<TimeSeriesRow>(sql,
                new { StartTime = startTime, EndTime = endTime },
                commandTimeout: _options.CommandTimeoutSeconds);

            return rows.Select(r => new DashboardTimeSeriesPoint
            {
                Timestamp = r.Timestamp,
                BotCount = r.BotCount,
                HumanCount = r.HumanCount,
                TotalCount = r.TotalCount
            }).ToList();
        }
        catch (Exception ex) when (_options.EnableTimescaleDB)
        {
            // time_bucket may not be available if TimescaleDB extension failed to load;
            // fall back to standard date_trunc
            _logger.LogWarning(ex, "TimescaleDB time_bucket query failed, falling back to date_trunc");
            var truncUnit = GetDateTruncUnit(bucketSize);
            var fallbackSql = $@"
                WITH time_buckets AS (
                    SELECT
                        date_trunc('{truncUnit}', timestamp) as bucket,
                        COUNT(*) FILTER (WHERE is_bot)::int as bot_count,
                        COUNT(*) FILTER (WHERE NOT is_bot)::int as human_count,
                        COUNT(*)::int as total_count
                    FROM dashboard_detections
                    WHERE timestamp >= @StartTime AND timestamp <= @EndTime
                    GROUP BY bucket
                    ORDER BY bucket
                )
                SELECT
                    bucket as Timestamp,
                    bot_count as BotCount,
                    human_count as HumanCount,
                    total_count as TotalCount
                FROM time_buckets";

            await using var fallbackConnection = new NpgsqlConnection(_options.ConnectionString);
            var fallbackRows = await fallbackConnection.QueryAsync<TimeSeriesRow>(fallbackSql,
                new { StartTime = startTime, EndTime = endTime },
                commandTimeout: _options.CommandTimeoutSeconds);

            return fallbackRows.Select(r => new DashboardTimeSeriesPoint
            {
                Timestamp = r.Timestamp,
                BotCount = r.BotCount,
                HumanCount = r.HumanCount,
                TotalCount = r.TotalCount
            }).ToList();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to get time series from PostgreSQL");
            throw;
        }
    }

    /// <summary>
    /// Maps a TimeSpan bucket size to the nearest PostgreSQL date_trunc unit.
    /// </summary>
    private static string GetDateTruncUnit(TimeSpan bucketSize)
    {
        return bucketSize.TotalMinutes switch
        {
            < 1 => "second",
            < 60 => "minute",
            < 1440 => "hour",  // < 1 day
            < 10080 => "day",  // < 1 week
            < 43200 => "week", // < 30 days
            _ => "month"
        };
    }

    // Mapping helpers
    private static DashboardDetectionEvent MapToDetectionEvent(DetectionRow row)
    {
        return new DashboardDetectionEvent
        {
            RequestId = row.RequestId,
            Timestamp = row.Timestamp,
            IsBot = row.IsBot,
            BotProbability = row.BotProbability,
            Confidence = row.Confidence,
            RiskBand = row.RiskBand,
            BotType = row.BotType,
            BotName = row.BotName,
            Action = row.Action,
            PolicyName = row.PolicyName,
            Method = row.Method,
            Path = row.Path,
            StatusCode = row.StatusCode,
            ProcessingTimeMs = row.ProcessingTimeMs,
            IpAddress = row.IpAddress,
            UserAgent = row.UserAgent,
            TopReasons = string.IsNullOrEmpty(row.TopReasons)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(row.TopReasons) ?? new List<string>(),
            PrimarySignature = row.PrimarySignature
        };
    }

    private static DashboardSignatureEvent MapToSignatureEvent(SignatureRow row)
    {
        return new DashboardSignatureEvent
        {
            SignatureId = row.SignatureId,
            Timestamp = row.Timestamp,
            PrimarySignature = row.PrimarySignature,
            IpSignature = row.IpSignature,
            UaSignature = row.UaSignature,
            ClientSideSignature = row.ClientSideSignature,
            FactorCount = row.FactorCount,
            RiskBand = row.RiskBand,
            HitCount = row.HitCount,
            IsKnownBot = row.IsKnownBot,
            BotName = row.BotName
        };
    }

    // Database row models
    private class DetectionRow
    {
        public string RequestId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public bool IsBot { get; set; }
        public double BotProbability { get; set; }
        public double Confidence { get; set; }
        public string RiskBand { get; set; } = string.Empty;
        public string? BotType { get; set; }
        public string? BotName { get; set; }
        public string? Action { get; set; }
        public string? PolicyName { get; set; }
        public string Method { get; set; } = string.Empty;
        public string Path { get; set; } = string.Empty;
        public int StatusCode { get; set; }
        public double ProcessingTimeMs { get; set; }
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public string? TopReasons { get; set; }
        public string? PrimarySignature { get; set; }
    }

    private class SignatureRow
    {
        public string SignatureId { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; }
        public string PrimarySignature { get; set; } = string.Empty;
        public string? IpSignature { get; set; }
        public string? UaSignature { get; set; }
        public string? ClientSideSignature { get; set; }
        public int FactorCount { get; set; }
        public string RiskBand { get; set; } = string.Empty;
        public int HitCount { get; set; }
        public bool IsKnownBot { get; set; }
        public string? BotName { get; set; }
    }

    private class TimeSeriesRow
    {
        public DateTime Timestamp { get; set; }
        public int BotCount { get; set; }
        public int HumanCount { get; set; }
        public int TotalCount { get; set; }
    }

    private class SummaryRow
    {
        public int TotalRequests { get; set; }
        public int BotRequests { get; set; }
        public int HumanRequests { get; set; }
        public int UncertainRequests { get; set; }
        public double AverageProcessingTimeMs { get; set; }
        public int UniqueSignatures { get; set; }
    }
}
