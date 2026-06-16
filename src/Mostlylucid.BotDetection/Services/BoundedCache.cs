using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Thread-safe bounded cache with TTL expiry and LFU eviction.
///     Use this instead of raw ConcurrentDictionary for all lookup caches
///     (DNS, ASN, CIDR, RDNS, honeypot, etc.) to prevent unbounded growth.
///     LFU (least-frequently-used) eviction keeps hot entries (e.g. popular
///     bot IPs verified every hour) while evicting cold entries first.
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

    public BoundedCache(int maxSize = 10_000, TimeSpan? defaultTtl = null)
    {
        _maxSize = maxSize;
        _defaultTtl = defaultTtl ?? TimeSpan.FromHours(1);
    }

    public int Count => _entries.Count;

    /// <summary>
    ///     Gets a value if present and not expired. Increments the hit counter
    ///     so frequently-accessed entries survive LFU eviction longer.
    ///     Returns false if missing or expired.
    /// </summary>
    public bool TryGet(TKey key, out TValue value)
    {
        if (_entries.TryGetValue(key, out var entry) && DateTime.UtcNow < entry.ExpiresAt)
        {
            Interlocked.Increment(ref entry.HitCount);
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
        // Preserve hit count when refreshing (e.g. DNS TTL renewal keeps popularity score).
        var prevHits = _entries.TryGetValue(key, out var existing) ? existing.HitCount : 0;
        _entries[key] = new CacheEntry { Value = value, ExpiresAt = DateTime.UtcNow + ttl, HitCount = prevHits };

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

            // Phase 2: If still over limit, evict the LFU quarter without a full sort.
            // A full O(N log N) sort over 4 000+ entries is expensive. Instead,
            // single-pass O(N) to find the median hit count, then remove all entries
            // below that threshold. This evicts the cold tail (LFU approximation)
            // while keeping the hot head, without materialising a sorted list.
            // Evict to 75% capacity so the next burst doesn't trigger immediately.
            if (_entries.Count > _maxSize)
            {
                // Snapshot to avoid ConcurrentDictionary mutation hazards.
                var snapshot = new List<KeyValuePair<TKey, CacheEntry>>(_entries.Count);
                foreach (var entry in _entries)
                    snapshot.Add(entry);

                var targetEvict = snapshot.Count - (_maxSize * 3 / 4);
                if (targetEvict > 0)
                {
                    // Partition-select: find the HitCount threshold below which
                    // we'd remove exactly targetEvict entries. One linear pass
                    // to collect all HitCount values, then the median/percentile.
                    var counts = new long[snapshot.Count];
                    for (var i = 0; i < snapshot.Count; i++)
                        counts[i] = snapshot[i].Value.HitCount;
                    Array.Sort(counts);
                    var threshold = counts[Math.Min(targetEvict - 1, counts.Length - 1)];

                    var removed = 0;
                    foreach (var kvp in snapshot)
                    {
                        if (removed >= targetEvict) break;
                        if (kvp.Value.HitCount <= threshold)
                        {
                            _entries.TryRemove(kvp.Key, out _);
                            removed++;
                        }
                    }
                }
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
        // Field (not property) so Interlocked.Increment can take ref.
        public long HitCount;
    }
}
