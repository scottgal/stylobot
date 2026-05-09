using System.Numerics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Bounded in-memory similarity search backed by SQLite centroids.
///     Replaces <see cref="HnswFileSimilaritySearch"/> to eliminate unbounded LOH growth.
///
///     Fast path (<see cref="FindSimilarAsync"/>): non-blocking TryGet on the hot cache then
///     SIMD cosine similarity. No SQLite I/O on the hot path.
///
///     Learning path (<see cref="AddAsync"/>): writes to the hot cache immediately, then fires
///     a background Task to upsert the centroid to SQLite.
/// </summary>
public sealed class SlimSignatureSimilaritySearch : ISignatureSimilaritySearch
{
    // Metadata stored alongside each vector in the cache.
    private sealed record CacheEntry(float[] Vector, bool WasBot, double Confidence);

    private readonly BoundedVectorCache<CacheEntry> _cache;
    private readonly ISignatureCentroidStore _centroidStore;
    private readonly ILogger<SlimSignatureSimilaritySearch> _logger;

    public SlimSignatureSimilaritySearch(
        ISignatureCentroidStore centroidStore,
        IOptions<BotDetectionOptions> options,
        ILogger<SlimSignatureSimilaritySearch> logger)
    {
        _centroidStore = centroidStore;
        _logger = logger;

        var cacheSize = options.Value.SelfMaintenance.SignatureCacheSize;
        _cache = new BoundedVectorCache<CacheEntry>(cacheSize);
    }

    // ------------------------------------------------------------------
    // ISignatureSimilaritySearch
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    public int Count => _cache.Count;

    /// <inheritdoc/>
    public Task<IReadOnlyList<SimilarSignature>> FindSimilarAsync(
        float[] vector,
        int topK = 5,
        float minSimilarity = 0.80f,
        string? embeddingContext = null)
    {
        var results = new List<SimilarSignature>();

        foreach (var kvp in _cache.GetAll())
        {
            var entry = kvp.Value;
            var sim = CosineSimilarity(vector, entry.Vector);
            if (sim >= minSimilarity)
                results.Add(new SimilarSignature(kvp.Key, 1f - sim, entry.WasBot, entry.Confidence));
        }

        // Sort by distance (closest first) and trim to topK
        results.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        return Task.FromResult<IReadOnlyList<SimilarSignature>>(results);
    }

    /// <inheritdoc/>
    public Task AddAsync(
        float[] vector,
        string signatureId,
        bool wasBot,
        double confidence,
        string? embeddingContext = null)
    {
        if (vector.Length == 0 || !IsValidVector(vector))
        {
            _logger.LogDebug("Skipping zero-norm or empty signature vector for {Signature}", signatureId);
            return Task.CompletedTask;
        }

        // Write to hot cache immediately (fast path)
        _cache.Set(signatureId, new CacheEntry(vector, wasBot, confidence), isBot: wasBot);

        // Persist to SQLite in background; never block the caller
        _ = Task.Run(async () =>
        {
            try
            {
                await _centroidStore.UpsertSignatureAsync(signatureId, vector, wasBot, confidence)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Background SQLite upsert failed for signature {Id}", signatureId);
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    ///     Returns a snapshot of all vectors currently in the hot cache.
    ///     Called by the compaction service.
    /// </summary>
    public IReadOnlyList<(string SignatureId, float[] Vector, bool WasBot, double Confidence)> GetAllVectorsSnapshot()
    {
        return _cache.GetAll()
            .Select(kvp => (kvp.Key, kvp.Value.Vector, kvp.Value.WasBot, kvp.Value.Confidence))
            .ToList();
    }

    /// <summary>
    ///     Bulk-replaces all vectors in the hot cache. Called by VectorCompactionService
    ///     after a compaction pass to repopulate from freshly computed centroids.
    /// </summary>
    public Task ReplaceAllAsync(
        IEnumerable<(string SignatureId, float[] Vector, bool WasBot, double Confidence)> vectors)
    {
        foreach (var (id, vec, wasBot, conf) in vectors)
            _cache.Set(id, new CacheEntry(vec, wasBot, conf), isBot: wasBot);

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    /// <remarks>No-op: persistence is handled by SQLite, not JSON files.</remarks>
    public Task SaveAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>No-op: startup warm-up is performed by <see cref="SessionVectorWarmupService"/> loading from SQLite.</remarks>
    public Task LoadAsync() => Task.CompletedTask;

    /// <summary>Returns the number of vectors in the cache.</summary>
    public Task<int> CountAsync() => Task.FromResult(_cache.Count);

    // ------------------------------------------------------------------
    // Internal helpers
    // ------------------------------------------------------------------

    private static bool IsValidVector(float[] v)
    {
        var normSq = 0f;
        foreach (var x in v) normSq += x * x;
        return normSq > 0f && !float.IsNaN(normSq) && !float.IsInfinity(normSq);
    }

    /// <summary>
    ///     SIMD-accelerated cosine similarity using <see cref="System.Numerics.Vector{T}"/>.
    ///     Returns values in [0, 1] for normalised vectors; unclamped for raw vectors.
    /// </summary>
    private static float CosineSimilarity(float[] a, float[] b)
    {
        var len = Math.Min(a.Length, b.Length);
        var vA = a.AsSpan(0, len);
        var vB = b.AsSpan(0, len);
        float dot = 0f, magA = 0f, magB = 0f;
        var i = 0;
        var simdLen = Vector<float>.Count;

        for (; i <= len - simdLen; i += simdLen)
        {
            var va = new Vector<float>(vA.Slice(i));
            var vb = new Vector<float>(vB.Slice(i));
            dot  += Vector.Dot(va, vb);
            magA += Vector.Dot(va, va);
            magB += Vector.Dot(vb, vb);
        }

        for (; i < len; i++)
        {
            dot  += a[i] * b[i];
            magA += a[i] * a[i];
            magB += b[i] * b[i];
        }

        var denom = MathF.Sqrt(magA) * MathF.Sqrt(magB);
        return denom < 1e-8f ? 0f : dot / denom;
    }
}
