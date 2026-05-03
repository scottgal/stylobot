using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Thread-safe bounded cache with TTL expiry and LRU eviction.
///     Use this instead of raw ConcurrentDictionary for all lookup caches
///     (DNS, ASN, CIDR, RDNS, honeypot, etc.) to prevent unbounded growth.
///
///     This is a lightweight alternative to IMemoryCache for cases where
///     the cache is internal to a service and doesn't need DI.
/// </summary>
public sealed class BoundedCache<TKey, TValue> where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, CacheEntry> _entries = new();
    private readonly int _maxSize;
    private readonly TimeSpan _defaultTtl;
    private readonly object _evictionLock = new();
    private long _accessCounter;

    public BoundedCache(int maxSize = 10_000, TimeSpan? defaultTtl = null)
    {
        _maxSize = maxSize;
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(1);
    }

    public int Count => _entries.Count;

    /// <summary>
    ///     Gets a value if present and not expired. Returns false if missing or expired.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
        {
            entry.LastAccessed = Interlocked.Increment(ref _accessCounter);
            value = entry.Value;
            return true;
        }

        value = default!;
        return false;
    }

    /// <summary>
    ///     Sets a value with the default TTL.
    /// </summary>
    public void Set(TKey key, TValue value)
    {
        Set(key, value, _defaultTtl);
    }

    /// <summary>
    ///     Sets a value with a specific TTL.
    /// </summary>
    public void Set(TKey key, TValue value, TimeSpan ttl)
    {
        _entries[key] = new CacheEntry
        {
            Value = value,
            ExpiresAt = DateTime.UtcNow + ttl,
            LastAccessed = Interlocked.Increment(ref _accessCounter)
        };

        EvictIfNeeded();
    }

    /// <summary>
    ///     Gets or adds a value. The factory is called if the key is missing or expired.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory)
    {
        return GetOrAdd(key, factory, _defaultTtl);
    }

    /// <summary>
    ///     Gets or adds a value with a specific TTL.
    /// </summary>
    public TValue GetOrAdd(TKey key, Func<TKey, TValue> factory, TimeSpan ttl)
    {
        if (TryGet(key, out var value))
            return value;

        value = factory(key);
        Set(key, value, ttl);
        return value;
    }

    /// <summary>
    ///     Async version of GetOrAdd.
    /// </summary>
    public async Task<TValue> GetOrAddAsync(TKey key, Func<TKey, Task<TValue>> factory, TimeSpan? ttl = null)
    {
        if (TryGet(key, out var value))
            return value;

        value = await factory(key);
        Set(key, value, ttl ?? _defaultTtl);
        return value;
    }

    /// <summary>
    ///     Removes a specific key.
    /// </summary>
    public bool Remove(TKey key) => _entries.TryRemove(key, out _);

    /// <summary>
    ///     Clears all entries. Use when the underlying data source changes
    ///     and all cached results are stale (e.g., CIDR ranges refresh).
    /// </summary>
    public void Clear() => _entries.Clear();

    private void EvictIfNeeded()
    {
        if (_entries.Count <= _maxSize) return;

        // Only one thread does eviction at a time
        if (!Monitor.TryEnter(_evictionLock)) return;
        try
        {
            var now = DateTime.UtcNow;

            // Phase 1: Remove expired entries
            var expiredKeys = new List<TKey>();
            foreach (var kvp in _entries)
            {
                if (now >= kvp.Value.ExpiresAt)
                    expiredKeys.Add(kvp.Key);
            }
            foreach (var key in expiredKeys)
                _entries.TryRemove(key, out _);

            // Phase 2: If still over limit, evict LRU (oldest accessed)
            if (_entries.Count > _maxSize)
            {
                // Avoid LINQ's ICollection.CopyTo fast path over ConcurrentDictionary:
                // under heavy mutation it can observe a stale count and throw.
                var snapshot = new List<KeyValuePair<TKey, CacheEntry>>(_entries.Count);
                foreach (var entry in _entries)
                    snapshot.Add(entry);

                var toEvict = snapshot
                    .OrderBy(kv => kv.Value.LastAccessed)
                    .Take(Math.Max(0, snapshot.Count - (_maxSize * 3 / 4))) // Evict down to 75% capacity
                    .Select(kv => kv.Key)
                    .ToList();

                foreach (var key in toEvict)
                    _entries.TryRemove(key, out _);
            }
        }
        finally
        {
            Monitor.Exit(_evictionLock);
        }
    }

    private sealed class CacheEntry
    {
        public required TValue Value { get; init; }
        public required DateTime ExpiresAt { get; init; }
        public long LastAccessed { get; set; }
    }
}
