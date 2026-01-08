using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.SignatureStore.Data;
using Mostlylucid.BotDetection.SignatureStore.Models;

namespace Mostlylucid.BotDetection.SignatureStore.Repositories;

/// <summary>
/// Repository implementation for querying signatures from Postgres.
/// Uses JSONB operators for efficient querying by any signal.
/// </summary>
public class SignatureRepository : ISignatureRepository
{
    private readonly SignatureStoreDbContext _context;
    private readonly ILogger<SignatureRepository> _logger;

    public SignatureRepository(
        SignatureStoreDbContext context,
        ILogger<SignatureRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task StoreSignatureAsync(SignatureEntity signature, CancellationToken cancellationToken = default)
    {
        try
        {
            _context.Signatures.Add(signature);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store signature {SignatureId}", signature.SignatureId);
            throw;
        }
    }

    public async Task StoreBatchAsync(
        IEnumerable<SignatureEntity> signatures,
        CancellationToken cancellationToken = default)
    {
        try
        {
            _context.Signatures.AddRange(signatures);
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to store batch of {Count} signatures", signatures.Count());
            throw;
        }
    }

    public async Task<List<SignatureQueryResult>> GetRecentAsync(
        int count = 100,
        CancellationToken cancellationToken = default)
    {
        return await _context.Signatures
            .OrderByDescending(s => s.Timestamp)
            .Take(count)
            .Select(s => new SignatureQueryResult
            {
                SignatureId = s.SignatureId,
                Timestamp = s.Timestamp,
                BotProbability = s.BotProbability,
                Confidence = s.Confidence,
                RiskBand = s.RiskBand,
                RequestPath = s.RequestPath,
                RemoteIp = s.RemoteIp,
                UserAgent = s.UserAgent,
                BotName = s.BotName,
                DetectorCount = s.DetectorCount,
                SignatureJson = s.SignatureJson
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<SignatureQueryResult>> GetTopByBotProbabilityAsync(
        int count = 100,
        CancellationToken cancellationToken = default)
    {
        return await _context.Signatures
            .OrderByDescending(s => s.BotProbability)
            .ThenByDescending(s => s.Timestamp)
            .Take(count)
            .Select(s => new SignatureQueryResult
            {
                SignatureId = s.SignatureId,
                Timestamp = s.Timestamp,
                BotProbability = s.BotProbability,
                Confidence = s.Confidence,
                RiskBand = s.RiskBand,
                RequestPath = s.RequestPath,
                RemoteIp = s.RemoteIp,
                UserAgent = s.UserAgent,
                BotName = s.BotName,
                DetectorCount = s.DetectorCount,
                SignatureJson = s.SignatureJson
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<List<SignatureQueryResult>> GetOrderedBySignalAsync(
        string signalPath,
        bool descending = true,
        int count = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        // Use raw SQL for JSONB path extraction (EF Core doesn't fully support ->> operator)
        // Example: ORDER BY signature_json->'signals'->'ua'->>'bot_probability' DESC

        var orderDirection = descending ? "DESC" : "ASC";

        // Build JSONB path (e.g., "signals.ua.bot_probability" -> ->'signals'->'ua'->>'bot_probability')
        var jsonbPath = BuildJsonbPath(signalPath);

        var sql = $@"
            SELECT
                signature_id as ""SignatureId"",
                timestamp as ""Timestamp"",
                bot_probability as ""BotProbability"",
                confidence as ""Confidence"",
                risk_band as ""RiskBand"",
                request_path as ""RequestPath"",
                remote_ip as ""RemoteIp"",
                user_agent as ""UserAgent"",
                bot_name as ""BotName"",
                detector_count as ""DetectorCount"",
                signature_json as ""SignatureJson""
            FROM bot_signatures
            ORDER BY (signature_json{jsonbPath})::text {orderDirection} NULLS LAST
            LIMIT {count} OFFSET {offset}
        ";

        return await _context.Database
            .SqlQueryRaw<SignatureQueryResult>(sql)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<SignatureQueryResult>> GetBySignalFilterAsync(
        string signalPath,
        object? signalValue = null,
        int count = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        // Use JSONB containment operator (@>) for filtering
        // Example: WHERE signature_json @> '{"signals": {"ua": {"headless_detected": true}}}'::jsonb

        var jsonbPath = BuildJsonbPath(signalPath);

        string whereClause;
        if (signalValue != null)
        {
            // Filter by specific value
            var valueJson = System.Text.Json.JsonSerializer.Serialize(signalValue);
            var containmentJson = BuildContainmentJson(signalPath, valueJson);
            whereClause = $"signature_json @> '{containmentJson}'::jsonb";
        }
        else
        {
            // Just check if path exists
            whereClause = $"signature_json{jsonbPath} IS NOT NULL";
        }

        var sql = $@"
            SELECT
                signature_id as ""SignatureId"",
                timestamp as ""Timestamp"",
                bot_probability as ""BotProbability"",
                confidence as ""Confidence"",
                risk_band as ""RiskBand"",
                request_path as ""RequestPath"",
                remote_ip as ""RemoteIp"",
                user_agent as ""UserAgent"",
                bot_name as ""BotName"",
                detector_count as ""DetectorCount"",
                signature_json as ""SignatureJson""
            FROM bot_signatures
            WHERE {whereClause}
            ORDER BY bot_probability DESC, timestamp DESC
            LIMIT {count} OFFSET {offset}
        ";

        return await _context.Database
            .SqlQueryRaw<SignatureQueryResult>(sql)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<SignatureQueryResult>> GetByRiskBandAsync(
        string riskBand,
        int count = 100,
        int offset = 0,
        CancellationToken cancellationToken = default)
    {
        return await _context.Signatures
            .Where(s => s.RiskBand == riskBand)
            .OrderByDescending(s => s.BotProbability)
            .ThenByDescending(s => s.Timestamp)
            .Skip(offset)
            .Take(count)
            .Select(s => new SignatureQueryResult
            {
                SignatureId = s.SignatureId,
                Timestamp = s.Timestamp,
                BotProbability = s.BotProbability,
                Confidence = s.Confidence,
                RiskBand = s.RiskBand,
                RequestPath = s.RequestPath,
                RemoteIp = s.RemoteIp,
                UserAgent = s.UserAgent,
                BotName = s.BotName,
                DetectorCount = s.DetectorCount,
                SignatureJson = s.SignatureJson
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<SignatureEntity?> GetByIdAsync(
        string signatureId,
        CancellationToken cancellationToken = default)
    {
        return await _context.Signatures
            .FirstOrDefaultAsync(s => s.SignatureId == signatureId, cancellationToken);
    }

    public async Task<SignatureStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        var stats = new SignatureStats();

        stats.TotalSignatures = await _context.Signatures.CountAsync(cancellationToken);

        if (stats.TotalSignatures == 0)
            return stats;

        stats.CountByRiskBand = await _context.Signatures
            .GroupBy(s => s.RiskBand)
            .Select(g => new { RiskBand = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.RiskBand, x => (long)x.Count, cancellationToken);

        var averages = await _context.Signatures
            .Select(s => new { s.BotProbability, s.Confidence })
            .ToListAsync(cancellationToken);

        stats.AverageBotProbability = averages.Average(a => a.BotProbability);
        stats.AverageConfidence = averages.Average(a => a.Confidence);

        stats.OldestSignature = await _context.Signatures
            .MinAsync(s => (DateTime?)s.Timestamp, cancellationToken);

        stats.NewestSignature = await _context.Signatures
            .MaxAsync(s => (DateTime?)s.Timestamp, cancellationToken);

        stats.ExpiredSignatures = await _context.Signatures
            .Where(s => s.ExpiresAt != null && s.ExpiresAt < DateTime.UtcNow)
            .CountAsync(cancellationToken);

        return stats;
    }

    public async Task<int> DeleteExpiredAsync(CancellationToken cancellationToken = default)
    {
        var deleted = await _context.Signatures
            .Where(s => s.ExpiresAt != null && s.ExpiresAt < DateTime.UtcNow)
            .ExecuteDeleteAsync(cancellationToken);

        _logger.LogInformation("Deleted {Count} expired signatures", deleted);
        return deleted;
    }

    public async Task<int> DeleteOlderThanAsync(DateTime cutoffDate, CancellationToken cancellationToken = default)
    {
        var deleted = await _context.Signatures
            .Where(s => s.Timestamp < cutoffDate)
            .ExecuteDeleteAsync(cancellationToken);

        _logger.LogInformation("Deleted {Count} signatures older than {CutoffDate}", deleted, cutoffDate);
        return deleted;
    }

    // Helper: Build JSONB path for SQL
    // "signals.ua.bot_probability" -> ->'signals'->'ua'->>'bot_probability'
    private static string BuildJsonbPath(string signalPath)
    {
        var parts = signalPath.Split('.');
        if (parts.Length == 0)
            return "";

        var path = string.Join("", parts.Take(parts.Length - 1).Select(p => $"->'{p}'"));
        path += $"->>'{parts.Last()}'"; // Last part uses ->> for text extraction

        return path;
    }

    // Helper: Build JSONB containment query
    // "signals.ua.headless_detected", "true" -> {"signals": {"ua": {"headless_detected": true}}}
    private static string BuildContainmentJson(string signalPath, string valueJson)
    {
        var parts = signalPath.Split('.');
        var json = valueJson;

        // Build nested JSON from innermost to outermost
        for (int i = parts.Length - 1; i >= 0; i--)
        {
            json = $"{{\"{parts[i]}\": {json}}}";
        }

        return json;
    }
}
