using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;

namespace Mostlylucid.Common.Caching;

/// <summary>
///     Memory-based caching service implementation
/// </summary>
/// <typeparam name="T">The type of value to cache</typeparam>
public class MemoryCachingService<T> : ICachingService<T> where T : class
{
    private readonly IMemoryCache _cache;
    private readonly string _keyPrefix;
    private readonly MemoryCachingOptions _options;
    private long _cacheHits;
    private long _totalLookups;

    public MemoryCachingService(
        IMemoryCache cache,
        IOptions<MemoryCachingOptions> options,
        string keyPrefix = "")
    {
        _cache = cache;
        _options = options.Value;
        _keyPrefix = string.IsNullOrEmpty(keyPrefix) ? typeof(T).Name : keyPrefix;
    }

    public async Task<T?> GetOrAddAsync(string key, Func<Task<T?>> factory,
        CancellationToken cancellationToken = default)
    {
        var fullKey = GetFullKey(key);
        Interlocked.Increment(ref _totalLookups);

        if (_cache.TryGetValue(fullKey, out T? cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        var result = await factory();

        if (result != null) Set(key, result);

        return result;
    }

    public T? Get(string key)
    {
        var fullKey = GetFullKey(key);
        Interlocked.Increment(ref _totalLookups);

        if (_cache.TryGetValue(fullKey, out T? cached))
        {
            Interlocked.Increment(ref _cacheHits);
            return cached;
        }

        return null;
    }

    public void Set(string key, T value)
    {
        var fullKey = GetFullKey(key);
        var cacheOptions = new MemoryCacheEntryOptions()
            .SetAbsoluteExpiration(_options.DefaultExpiration)
            .SetSize(_options.EntrySize);

        if (_options.SlidingExpiration.HasValue) cacheOptions.SetSlidingExpiration(_options.SlidingExpiration.Value);

        _cache.Set(fullKey, value, cacheOptions);
    }

    public void Remove(string key)
    {
        var fullKey = GetFullKey(key);
        _cache.Remove(fullKey);
    }

    public CacheStatistics GetStatistics()
    {
        return new CacheStatistics
        {
            TotalLookups = _totalLookups,
            CacheHits = _cacheHits
        };
    }

    private string GetFullKey(string key)
    {
        return $"{_keyPrefix}:{key}";
    }
}

/// <summary>
///     Options for memory caching
/// </summary>
public class MemoryCachingOptions
{
    /// <summary>
    ///     Default cache expiration time
    /// </summary>
    public TimeSpan DefaultExpiration { get; set; } = TimeSpan.FromHours(1);

    /// <summary>
    ///     Optional sliding expiration
    /// </summary>
    public TimeSpan? SlidingExpiration { get; set; }

    /// <summary>
    ///     Size of each cache entry (for size-limited caches)
    /// </summary>
    public int EntrySize { get; set; } = 1;

    /// <summary>
    ///     Maximum number of entries (requires SizeLimit on MemoryCache)
    /// </summary>
    public int MaxEntries { get; set; } = 10000;
}