namespace Mostlylucid.Common.Services;

/// <summary>
///     Base statistics interface for services
/// </summary>
public interface IServiceStatistics
{
    /// <summary>
    ///     Total number of requests/lookups
    /// </summary>
    long TotalRequests { get; }

    /// <summary>
    ///     Number of cache hits
    /// </summary>
    long CacheHits { get; }

    /// <summary>
    ///     Cache hit rate (0.0 - 1.0)
    /// </summary>
    double CacheHitRate { get; }
}

/// <summary>
///     Extended statistics for services with database backing
/// </summary>
public interface IDatabaseStatistics : IServiceStatistics
{
    /// <summary>
    ///     Whether the database is loaded and available
    /// </summary>
    bool DatabaseLoaded { get; }

    /// <summary>
    ///     Path to the database file
    /// </summary>
    string? DatabasePath { get; }

    /// <summary>
    ///     Last time the database was updated
    /// </summary>
    DateTime? LastDatabaseUpdate { get; }

    /// <summary>
    ///     Number of entries in the database/cache
    /// </summary>
    int CachedEntries { get; }
}

/// <summary>
///     Base implementation for tracking service statistics
/// </summary>
public class ServiceStatisticsTracker : IServiceStatistics
{
    private long _cacheHits;
    private long _totalRequests;

    public long TotalRequests => _totalRequests;
    public long CacheHits => _cacheHits;
    public double CacheHitRate => _totalRequests > 0 ? (double)_cacheHits / _totalRequests : 0;

    /// <summary>
    ///     Increment request counter
    /// </summary>
    public void IncrementRequests()
    {
        Interlocked.Increment(ref _totalRequests);
    }

    /// <summary>
    ///     Increment cache hit counter (also increments requests)
    /// </summary>
    public void IncrementCacheHit()
    {
        Interlocked.Increment(ref _totalRequests);
        Interlocked.Increment(ref _cacheHits);
    }

    /// <summary>
    ///     Reset all counters
    /// </summary>
    public void Reset()
    {
        Interlocked.Exchange(ref _totalRequests, 0);
        Interlocked.Exchange(ref _cacheHits, 0);
    }
}