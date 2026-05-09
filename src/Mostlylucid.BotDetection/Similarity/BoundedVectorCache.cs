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

    /// <summary>Pure read — does not bump frequency. Use <see cref="Touch"/> after a lookup that should count as an access.</summary>
    public bool TryGet(string key, out TValue? value)
    {
        if (_cache.TryGetValue(key, out var entry))
        {
            value = entry.Value;
            return true;
        }
        value = default;
        return false;
    }

    public void Set(string key, TValue value, bool isBot = false)
    {
        // Count and insert are not atomic; under concurrent writers the cache may
        // transiently hold one extra entry. Best-effort bounded semantics are acceptable here.
        if (_cache.Count >= _maxSize && !_cache.ContainsKey(key))
            Evict();
        _cache[key] = new Entry(value, isBot, 1);
    }

    /// <summary>Bumps the LFU frequency counter for an entry that was accessed via <see cref="GetAll"/>.</summary>
    public void Touch(string key)
    {
        if (_cache.TryGetValue(key, out var entry))
            _cache.TryUpdate(key, entry with { Frequency = entry.Frequency + 1 }, entry);
    }

    public void Clear() => _cache.Clear();

    public IEnumerable<KeyValuePair<string, TValue>> GetAll()
    {
        foreach (var kvp in _cache)
            yield return new KeyValuePair<string, TValue>(kvp.Key, kvp.Value.Value);
    }

    public int Count => _cache.Count;

    /// <summary>
    ///     Linear scan to find and remove the entry with the lowest retention score.
    ///     Bots get a 2x weight; humans get 1x. O(n) with zero allocations.
    /// </summary>
    private void Evict()
    {
        string? worstKey = null;
        double worstScore = double.MaxValue;
        foreach (var kvp in _cache)
        {
            var score = kvp.Value.Frequency * (kvp.Value.IsBot ? 2.0 : 1.0);
            if (score < worstScore) { worstScore = score; worstKey = kvp.Key; }
        }
        if (worstKey != null)
            _cache.TryRemove(worstKey, out _);
    }
}
