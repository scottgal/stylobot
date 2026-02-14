using System.Collections.Concurrent;
using Dapper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Services;
using Mostlylucid.BotDetection.UI.PostgreSQL.Configuration;
using Npgsql;

namespace Mostlylucid.BotDetection.UI.PostgreSQL.Storage;

/// <summary>
///     Provides cached 90-day historical reputation data from TimescaleDB.
///     Single efficient query per signature, cached for 5 minutes.
/// </summary>
public class TimescaleReputationProvider : ITimescaleReputationProvider
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (TimescaleReputationData Data, DateTimeOffset ExpiresAt)> _cache = new();
    private readonly ILogger<TimescaleReputationProvider> _logger;
    private readonly PostgreSQLStorageOptions _options;

    public TimescaleReputationProvider(
        ILogger<TimescaleReputationProvider> logger,
        IOptions<PostgreSQLStorageOptions> options)
    {
        _logger = logger;
        _options = options.Value;
    }

    public async Task<TimescaleReputationData?> GetReputationAsync(string primarySignature, CancellationToken ct = default)
    {
        // Check cache first
        if (_cache.TryGetValue(primarySignature, out var cached) && cached.ExpiresAt > DateTimeOffset.UtcNow)
            return cached.Data;

        try
        {
            await using var conn = new NpgsqlConnection(_options.ConnectionString);
            await conn.OpenAsync(ct);

            const string sql = """
                SELECT
                    COUNT(*) as total_count,
                    COUNT(*) FILTER (WHERE is_bot) as bot_count,
                    COALESCE(AVG(bot_probability), 0) as avg_bot_prob,
                    MIN(timestamp) as first_seen,
                    MAX(timestamp) as last_seen,
                    COUNT(DISTINCT DATE(timestamp)) as days_active,
                    COUNT(*) FILTER (WHERE timestamp > NOW() - INTERVAL '1 hour') as recent_hour_count
                FROM dashboard_detections
                WHERE primary_signature = @Signature
                    AND timestamp > NOW() - INTERVAL '90 days'
                """;

            var row = await conn.QuerySingleOrDefaultAsync<dynamic>(
                sql,
                new { Signature = primarySignature },
                commandTimeout: 5);

            if (row == null || (long)row.total_count == 0)
                return null;

            var totalCount = (long)row.total_count;
            var botCount = (long)row.bot_count;

            var data = new TimescaleReputationData
            {
                BotRatio = totalCount > 0 ? (double)botCount / totalCount : 0,
                TotalHitCount = (int)totalCount,
                DaysActive = (int)(long)row.days_active,
                RecentHourHitCount = (int)(long)row.recent_hour_count,
                AverageBotProbability = (double)(decimal)row.avg_bot_prob,
                FirstSeen = new DateTimeOffset((DateTime)row.first_seen, TimeSpan.Zero),
                LastSeen = new DateTimeOffset((DateTime)row.last_seen, TimeSpan.Zero)
            };

            // Cache the result
            _cache[primarySignature] = (data, DateTimeOffset.UtcNow.Add(CacheTtl));

            return data;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch TimescaleDB reputation for {Signature}", primarySignature);
            return null;
        }
    }

    public void InvalidateCache(string primarySignature)
    {
        _cache.TryRemove(primarySignature, out _);
    }
}
