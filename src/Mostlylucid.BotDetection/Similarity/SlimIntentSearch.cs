using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Bounded in-memory intent similarity search backed by <see cref="IIntentCentroidStore"/>.
///     Replaces <see cref="HnswIntentSearch"/> to eliminate unbounded LOH growth and file-system
///     dependency.
///
///     Fast path (<see cref="FindSimilarAsync"/>): non-blocking scan of the hot cache with
///     SIMD cosine similarity. No SQLite I/O on the hot path.
///
///     Learning path (<see cref="AddAsync"/>): writes to the hot cache immediately, then fires
///     a background Task to upsert the intent centroid to SQLite.
/// </summary>
public sealed class SlimIntentSearch : IIntentSimilaritySearch
{
    // Metadata stored alongside each vector in the cache.
    private sealed record CacheEntry(float[] Vector, double ThreatScore, string IntentCategory);

    private readonly BoundedVectorCache<CacheEntry> _cache;
    private readonly IIntentCentroidStore _centroidStore;
    private readonly ILogger<SlimIntentSearch> _logger;

    public SlimIntentSearch(
        IIntentCentroidStore centroidStore,
        IOptions<BotDetectionOptions> options,
        ILogger<SlimIntentSearch> logger)
    {
        _centroidStore = centroidStore;
        _logger = logger;

        var cacheSize = options.Value.SelfMaintenance.IntentCacheSize;
        _cache = new BoundedVectorCache<CacheEntry>(cacheSize);
    }

    // ------------------------------------------------------------------
    // IIntentSimilaritySearch
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    public int Count => _cache.Count;

    /// <inheritdoc/>
    public Task<IReadOnlyList<SimilarIntent>> FindSimilarAsync(
        float[] vector,
        int topK = 5,
        float minSimilarity = 0.75f)
    {
        var results = new List<SimilarIntent>();

        foreach (var kvp in _cache.GetAll())
        {
            var entry = kvp.Value;
            var sim = CosineSimilarity(vector, entry.Vector);
            if (sim >= minSimilarity)
            {
                _cache.Touch(kvp.Key);
                results.Add(new SimilarIntent(kvp.Key, 1f - sim, entry.ThreatScore, entry.IntentCategory));
            }
        }

        // Sort by distance (closest first) and trim to topK
        results.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        return Task.FromResult<IReadOnlyList<SimilarIntent>>(results);
    }

    /// <inheritdoc/>
    public Task AddAsync(
        float[] vector,
        string signatureId,
        double threatScore,
        string intentCategory,
        string? reasoning = null)
    {
        if (vector.Length == 0 || !IsValidVector(vector))
        {
            _logger.LogDebug("Skipping zero-norm or empty intent vector for {Signature}", signatureId);
            return Task.CompletedTask;
        }

        // Write to hot cache immediately (fast path)
        _cache.Set(signatureId, new CacheEntry(vector, threatScore, intentCategory));

        // Persist to SQLite in background; never block the caller
        _ = Task.Run(async () =>
        {
            try
            {
                await _centroidStore.UpsertIntentAsync(signatureId, vector, threatScore, intentCategory)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background SQLite upsert failed for intent signature {Id}", signatureId);
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Bulk-replaces all vectors in the hot cache. Called after a compaction pass to
    ///     repopulate from freshly computed centroids.
    /// </summary>
    public Task ReplaceAllAsync(
        IEnumerable<(string SignatureId, float[] Vector, double ThreatScore, string IntentCategory)> vectors)
    {
        _cache.Clear();
        foreach (var (id, vec, score, category) in vectors)
            _cache.Set(id, new CacheEntry(vec, score, category));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>No-op: persistence is handled by SQLite, not JSON files.</remarks>
    public Task SaveAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>No-op: startup warm-up is performed by loading from SQLite via the centroid store.</remarks>
    public Task LoadAsync() => Task.CompletedTask;

    private static bool IsValidVector(float[] v) => VectorMath.IsValidVector(v);
    private static float CosineSimilarity(float[] a, float[] b) => VectorMath.CosineSimilarity(a, b);
}
