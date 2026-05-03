using System.Collections.Concurrent;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Llm.Holodeck;

public sealed class HolodeckResponseCache
{
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly int _maxSize;
    private readonly TimeSpan _ttl;
    private long _insertOrder;

    public HolodeckResponseCache(int maxSize, TimeSpan ttl)
    {
        _maxSize = maxSize;
        _ttl = ttl;
    }

    public int Count => _cache.Count;

    public bool TryGet(string fingerprint, string path, out HolodeckResponse? response)
    {
        response = null;
        var key = $"{fingerprint}:{path}";
        if (!_cache.TryGetValue(key, out var entry)) return false;
        if (DateTime.UtcNow - entry.CreatedAt > _ttl)
        {
            _cache.TryRemove(key, out _);
            return false;
        }
        response = entry.Response;
        return true;
    }

    public void Set(string fingerprint, string path, HolodeckResponse response)
    {
        var key = $"{fingerprint}:{path}";
        while (_cache.Count >= _maxSize)
        {
            var oldest = _cache.OrderBy(kv => kv.Value.Order).FirstOrDefault();
            if (oldest.Key != null) _cache.TryRemove(oldest.Key, out _);
            else break;
        }
        _cache[key] = new CacheEntry(response, DateTime.UtcNow, Interlocked.Increment(ref _insertOrder));
    }

    private sealed record CacheEntry(HolodeckResponse Response, DateTime CreatedAt, long Order);
}
