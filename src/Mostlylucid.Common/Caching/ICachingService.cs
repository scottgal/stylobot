namespace Mostlylucid.Common.Caching;

/// <summary>
///     Generic caching service abstraction for consistent caching across services
/// </summary>
/// <typeparam name="T">The type of value to cache</typeparam>
public interface ICachingService<T> where T : class
{
    /// <summary>
    ///     Get a value from cache, or add it using the factory if not present
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="factory">Factory to create value if not cached</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The cached or newly created value</returns>
    Task<T?> GetOrAddAsync(string key, Func<Task<T?>> factory, CancellationToken cancellationToken = default);

    /// <summary>
    ///     Get a value from cache
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <returns>The cached value or null</returns>
    T? Get(string key);

    /// <summary>
    ///     Set a value in cache
    /// </summary>
    /// <param name="key">Cache key</param>
    /// <param name="value">Value to cache</param>
    void Set(string key, T value);

    /// <summary>
    ///     Remove a value from cache
    /// </summary>
    /// <param name="key">Cache key</param>
    void Remove(string key);

    /// <summary>
    ///     Get cache statistics
    /// </summary>
    CacheStatistics GetStatistics();
}

/// <summary>
///     Cache statistics
/// </summary>
public class CacheStatistics
{
    /// <summary>
    ///     Total number of cache lookups
    /// </summary>
    public long TotalLookups { get; set; }

    /// <summary>
    ///     Number of cache hits
    /// </summary>
    public long CacheHits { get; set; }

    /// <summary>
    ///     Number of cache misses
    /// </summary>
    public long CacheMisses => TotalLookups - CacheHits;

    /// <summary>
    ///     Cache hit rate (0.0 - 1.0)
    /// </summary>
    public double HitRate => TotalLookups > 0 ? (double)CacheHits / TotalLookups : 0;
}