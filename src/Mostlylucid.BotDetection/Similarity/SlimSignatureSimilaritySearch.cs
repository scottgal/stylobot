using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data.Centroids;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Bounded in-memory similarity search backed by SQLite centroids.
///     Replaces the prior <c>HnswFileSimilaritySearch</c> to eliminate unbounded LOH growth.
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
    private readonly ICentroidWriter _centroidWriter;
    private readonly CentroidWriterOptions _centroidOpts;
    private readonly ILogger<SlimSignatureSimilaritySearch> _logger;

    public SlimSignatureSimilaritySearch(
        IOptions<BotDetectionOptions> options,
        ICentroidWriter centroidWriter,
        IOptions<CentroidWriterOptions> centroidOptions,
        ILogger<SlimSignatureSimilaritySearch> logger)
    {
        _centroidWriter = centroidWriter;
        _centroidOpts = centroidOptions.Value;
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
            {
                _cache.Touch(kvp.Key);
                results.Add(new SimilarSignature(kvp.Key, 1f - sim, entry.WasBot, entry.Confidence));
            }
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

        // LFU-sampled synchronous enqueue: borderline or high-threat entries are worth
        // persisting; confident-harmless entries are shed first. Non-blocking: no Task.Run.
        var necessity = DecisionNecessity.Value(
            botProbability: confidence,
            threat: wasBot ? confidence : 0.0,
            ageSeconds: 0,
            threshold: _centroidOpts.DecisionThreshold,
            halfLifeSeconds: _centroidOpts.DecisionHalfLifeSeconds);
        if (necessity >= _centroidOpts.SamplingThreshold)
            _centroidWriter.Enqueue(new CentroidWriteMessage.SignatureCentroidWrite(
                signatureId, vector, wasBot, confidence));

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
    /// <remarks>No-op: startup warm-up is performed by <see cref="Mostlylucid.BotDetection.Services.SessionVectorWarmupService"/> loading from SQLite.</remarks>
    public Task LoadAsync() => Task.CompletedTask;

    private static bool IsValidVector(float[] v) => VectorMath.IsValidVector(v);
    private static float CosineSimilarity(float[] a, float[] b) => VectorMath.CosineSimilarity(a, b);
}
