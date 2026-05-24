using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Grouping;

/// <summary>
///     Default <see cref="ISessionSimilarityLookup"/>. Maintains an
///     in-memory cache of (signature -> nearest-bot-neighbour, expiry)
///     populated lazily. Cache misses trigger a fire-and-forget background
///     populate; the first call for a fresh signature returns null and
///     subsequent calls within the TTL return the cached neighbour.
/// </summary>
/// <remarks>
///     <para>
///         Memory model:
///         <list type="bullet">
///             <item>One entry per known signature, capped at <see cref="MaxEntries"/>.</item>
///             <item>Entries expire after <see cref="CacheTtlSeconds"/>; expired entries
///                   are refreshed lazily on next lookup.</item>
///             <item>LRU eviction when above cap.</item>
///         </list>
///     </para>
///     <para>
///         The lookup never blocks. If the cache doesn't have a fresh
///         answer, the grouper falls through to a lower tier
///         (typically subnet rotation or friendly name) for this render
///         and the cache fills in for the next render.
///     </para>
/// </remarks>
public sealed class BoundedSessionSimilarityLookup : ISessionSimilarityLookup
{
    public const int MaxEntries = 5_000;
    public const int CacheTtlSeconds = 60;

    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _inFlight = new(StringComparer.Ordinal);
    private readonly ISessionVectorSearch _search;
    private readonly ILogger<BoundedSessionSimilarityLookup> _logger;

    public BoundedSessionSimilarityLookup(
        ISessionVectorSearch search,
        ILogger<BoundedSessionSimilarityLookup> logger)
    {
        _search = search;
        _logger = logger;
    }

    public SimilarityNeighbour? FindNearestBotNeighbour(string signature, float minCosine)
    {
        if (string.IsNullOrEmpty(signature)) return null;

        if (_cache.TryGetValue(signature, out var entry) && entry.ExpiresUtc > DateTime.UtcNow)
        {
            if (entry.Neighbour is not null && entry.Neighbour.Cosine >= minCosine)
                return entry.Neighbour;
            return null;
        }

        // Cold or expired -- kick off a populate in the background and return null.
        // Cheap dedupe: only one in-flight populate per signature.
        if (_inFlight.TryAdd(signature, 1))
        {
            _ = Task.Run(() => PopulateAsync(signature));
        }
        return null;
    }

    private async Task PopulateAsync(string signature)
    {
        try
        {
            var snapshot = _search.GetAllVectorsSnapshot();
            float[]? selfVector = null;
            foreach (var (vec, meta) in snapshot)
            {
                if (string.Equals(meta.Signature, signature, StringComparison.Ordinal))
                {
                    selfVector = vec;
                    break;
                }
            }
            if (selfVector is null)
            {
                Insert(signature, null);
                return;
            }

            // Top-2: nearest is self, second-nearest is the actual neighbour.
            var matches = await _search.FindSimilarAsync(selfVector, topK: 2, minSimilarity: 0.0f);
            SimilarityNeighbour? neighbour = null;
            foreach (var m in matches)
            {
                if (string.Equals(m.Signature, signature, StringComparison.Ordinal)) continue;
                neighbour = new SimilarityNeighbour(m.Signature, m.Similarity);
                break;
            }
            Insert(signature, neighbour);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SessionSimilarityLookup populate failed for {Sig}", signature);
            Insert(signature, null);
        }
        finally
        {
            _inFlight.TryRemove(signature, out _);
        }
    }

    private void Insert(string signature, SimilarityNeighbour? neighbour)
    {
        if (_cache.Count > MaxEntries) EvictOldest();
        _cache[signature] = new CacheEntry(neighbour, DateTime.UtcNow.AddSeconds(CacheTtlSeconds));
    }

    private void EvictOldest()
    {
        var target = MaxEntries - (MaxEntries / 10);
        if (_cache.Count <= target) return;

        var oldest = _cache
            .OrderBy(kv => kv.Value.ExpiresUtc)
            .Take(_cache.Count - target)
            .Select(kv => kv.Key)
            .ToArray();
        foreach (var k in oldest) _cache.TryRemove(k, out _);
    }

    private sealed record CacheEntry(SimilarityNeighbour? Neighbour, DateTime ExpiresUtc);
}
