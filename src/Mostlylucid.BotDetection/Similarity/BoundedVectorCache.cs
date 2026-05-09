using System.Collections.Concurrent;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Thread-safe bounded cache wrapping <see cref="ConcurrentDictionary{TKey,TValue}"/>.
///     When the cache is at capacity, the lowest-frequency entry is evicted (LFU semantics).
///     Bot-flagged entries receive a 2x retention multiplier to survive longer.
/// </summary>
internal sealed class BoundedVectorCache<TValue>
{
    private sealed record Entry(TValue Value, bool IsBot, long Frequency);

    private readonly ConcurrentDictionary<string, Entry> _cache = new();
    private readonly int _maxSize;

    public BoundedVectorCache(int maxSize) => _maxSize = maxSize;

    public bool TryGet(string key, out TValue? value)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            // Increment frequency counter (best-effort; ABA race is harmless here)
            _cache.TryUpdate(key, entry with { Frequency = entry.Frequency + 1 }, entry);
            value = entry.Value;
            return true;
        }
        value = default;
        return false;
    }

    public void Set(string key, TValue value, bool isBot = false)
    {
        if (_cache.Count >= _maxSize && !_cache.ContainsKey(key))
            Evict();
        _cache[key] = new Entry(value, isBot, 1);
    }

    public IEnumerable<KeyValuePair<string, TValue>> GetAll() =>
        _cache.Select(kvp => new KeyValuePair<string, TValue>(kvp.Key, kvp.Value.Value));

    public int Count => _cache.Count;

    /// <summary>
    ///     Removes the entry with the lowest retention score.
    ///     Bots get a 2x weight; humans get 1x.
    /// </summary>
    private void Evict()
    {
        var toEvict = _cache
            .OrderBy(kvp => kvp.Value.Frequency * (kvp.Value.IsBot ? 2.0 : 1.0))
            .FirstOrDefault();
        if (toEvict.Key != null)
            _cache.TryRemove(toEvict.Key, out _);
    }
}
