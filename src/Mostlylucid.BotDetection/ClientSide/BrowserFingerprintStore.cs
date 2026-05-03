using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.ClientSide;

/// <summary>
///     In-memory store for browser fingerprint results.
///     Results are correlated by IP hash and stored for a configurable duration.
/// </summary>
public class BrowserFingerprintStore : IBrowserFingerprintStore
{
    private const string CachePrefix = "MLBotD:Fingerprint:";
    private readonly IMemoryCache _cache;
    private readonly BotDetectionOptions _options;

    public BrowserFingerprintStore(
        IMemoryCache cache,
        IOptions<BotDetectionOptions> options)
    {
        _cache = cache;
        _options = options.Value;
    }

    public void Store(string ipHash, BrowserFingerprintResult result)
    {
        var cacheKey = $"{CachePrefix}{ipHash}";
        var expiry = TimeSpan.FromSeconds(_options.ClientSide.FingerprintCacheDurationSeconds);
        _cache.Set(cacheKey, result, expiry);
    }

    public BrowserFingerprintResult? Get(string ipHash)
    {
        var cacheKey = $"{CachePrefix}{ipHash}";
        return _cache.TryGetValue(cacheKey, out BrowserFingerprintResult? result) ? result : null;
    }
}