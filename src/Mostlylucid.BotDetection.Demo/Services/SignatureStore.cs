using System.Collections.Concurrent;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Orchestration;

namespace Mostlylucid.BotDetection.Demo.Services;

/// <summary>
///     In-memory store for bot detection signatures.
///     Stores full signature data for demo purposes, allowing retrieval by signature ID.
/// </summary>
public class SignatureStore
{
    private readonly ILogger<SignatureStore> _logger;
    private readonly int _maxSignatures;
    private readonly ConcurrentDictionary<string, StoredSignature> _signatures = new();

    public SignatureStore(ILogger<SignatureStore> logger, int maxSignatures = 10000)
    {
        _logger = logger;
        _maxSignatures = maxSignatures;
    }

    /// <summary>
    ///     Store a signature from bot detection result
    /// </summary>
    public void StoreSignature(string signatureId, AggregatedEvidence evidence, HttpContext httpContext)
    {
        // Evict oldest if at capacity
        if (_signatures.Count >= _maxSignatures)
        {
            var oldest = _signatures.OrderBy(kvp => kvp.Value.Timestamp).FirstOrDefault();
            if (oldest.Key != null) _signatures.TryRemove(oldest.Key, out _);
        }

        var stored = new StoredSignature
        {
            SignatureId = signatureId,
            Timestamp = DateTime.UtcNow,
            Evidence = evidence,
            RequestMetadata = ExtractRequestMetadata(httpContext)
        };

        _signatures[signatureId] = stored;

        _logger.LogTrace(
            "Stored signature {SignatureId} - BotProb: {BotProb:F2}, Type: {BotType}",
            signatureId,
            evidence.BotProbability,
            evidence.PrimaryBotType);
    }

    /// <summary>
    ///     Retrieve a signature by ID
    /// </summary>
    public StoredSignature? GetSignature(string signatureId)
    {
        return _signatures.TryGetValue(signatureId, out var signature) ? signature : null;
    }

    /// <summary>
    ///     Get recent signatures (for streaming/display)
    /// </summary>
    public List<StoredSignature> GetRecentSignatures(int count = 50)
    {
        return _signatures.Values
            .OrderByDescending(s => s.Timestamp)
            .Take(count)
            .ToList();
    }

    /// <summary>
    ///     Get statistics about stored signatures
    /// </summary>
    public SignatureStoreStats GetStats()
    {
        var signatures = _signatures.Values.ToList();

        return new SignatureStoreStats
        {
            TotalSignatures = signatures.Count,
            BotCount = signatures.Count(s => s.Evidence.BotProbability > 0.5),
            HumanCount = signatures.Count(s => s.Evidence.BotProbability <= 0.5),
            AvgBotProbability = signatures.Any() ? signatures.Average(s => s.Evidence.BotProbability) : 0,
            TopBotTypes = signatures
                .Where(s => s.Evidence.PrimaryBotType.HasValue)
                .GroupBy(s => s.Evidence.PrimaryBotType!.Value)
                .Select(g => new BotTypeCount
                {
                    BotType = g.Key,
                    Count = g.Count()
                })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToList()
        };
    }

    private static RequestMetadata ExtractRequestMetadata(HttpContext context)
    {
        return new RequestMetadata
        {
            Path = context.Request.Path.ToString(),
            Method = context.Request.Method,
            UserAgent = context.Request.Headers.UserAgent.ToString(),
            RemoteIp = context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            Protocol = context.Request.Protocol,
            Headers = context.Request.Headers
                .Where(h => IsInterestingHeader(h.Key))
                .ToDictionary(h => h.Key, h => h.Value.ToString())
        };
    }

    private static bool IsInterestingHeader(string headerName)
    {
        var interesting = new[]
        {
            "user-agent", "accept", "accept-language", "accept-encoding",
            "referer", "x-forwarded-for", "x-real-ip",
            "x-tls-protocol", "x-tls-cipher", "x-http-protocol",
            "x-bot-detected", "x-bot-confidence", "x-bot-type"
        };

        return interesting.Contains(headerName.ToLowerInvariant());
    }
}

/// <summary>
///     Signature stored in memory for demo display
/// </summary>
public class StoredSignature
{
    public required string SignatureId { get; init; }
    public required DateTime Timestamp { get; init; }
    public required AggregatedEvidence Evidence { get; init; }
    public required RequestMetadata RequestMetadata { get; init; }
}

/// <summary>
///     Request metadata captured with signature
/// </summary>
public class RequestMetadata
{
    public required string Path { get; init; }
    public required string Method { get; init; }
    public required string UserAgent { get; init; }
    public required string RemoteIp { get; init; }
    public required string Protocol { get; init; }
    public required Dictionary<string, string> Headers { get; init; }
}

/// <summary>
///     Statistics about stored signatures
/// </summary>
public class SignatureStoreStats
{
    public int TotalSignatures { get; init; }
    public int BotCount { get; init; }
    public int HumanCount { get; init; }
    public double AvgBotProbability { get; init; }
    public List<BotTypeCount> TopBotTypes { get; init; } = new();
}

public class BotTypeCount
{
    public BotType BotType { get; init; }
    public int Count { get; init; }
}