using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Analysis;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Centroids;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Similarity;

/// <summary>
///     Bounded in-memory similarity search backed by <see cref="ISessionCentroidStore"/> (SQLite).
///     Replaces the prior <c>HnswSessionVectorSearch</c> to eliminate HNSW graph overhead and unbounded
///     LOH growth. No file I/O on the hot path.
///
///     Fast path (<see cref="FindSimilarAsync"/>): non-blocking SIMD cosine scan over the hot cache.
///     Learning path (<see cref="AddAsync"/>): writes to the hot cache immediately, then fires a
///     background Task to upsert the centroid row to SQLite.
/// </summary>
public sealed class SlimSessionVectorSearch : ISessionVectorSearch
{
    private sealed record CacheEntry(
        float[] Vector,
        bool IsBot,
        double BotProbability,
        SessionVectorMetadata Metadata);

    private static readonly int ExpectedDimensions = SessionVectorizer.Dimensions;

    private readonly BoundedVectorCache<CacheEntry> _cache;
    private readonly ICentroidWriter _centroidWriter;
    private readonly CentroidWriterOptions _centroidOpts;
    private readonly ILogger<SlimSessionVectorSearch> _logger;

    public SlimSessionVectorSearch(
        IOptions<BotDetectionOptions> options,
        ICentroidWriter centroidWriter,
        IOptions<CentroidWriterOptions> centroidOptions,
        ILogger<SlimSessionVectorSearch> logger)
    {
        _centroidWriter = centroidWriter;
        _centroidOpts = centroidOptions.Value;
        _logger = logger;

        var cacheSize = options.Value.SelfMaintenance.SessionCacheSize;
        _cache = new BoundedVectorCache<CacheEntry>(cacheSize);
    }

    // ------------------------------------------------------------------
    // ISessionVectorSearch
    // ------------------------------------------------------------------

    /// <inheritdoc/>
    public int Count => _cache.Count;

    /// <inheritdoc/>
    public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarAsync(
        float[] vector, int topK = 10, float minSimilarity = 0.70f)
    {
        var results = new List<SessionVectorMatch>();

        foreach (var kvp in _cache.GetAll())
        {
            var entry = kvp.Value;
            var sim = CosineSimilarity(vector, entry.Vector);
            if (sim >= minSimilarity)
            {
                _cache.Touch(kvp.Key);
                results.Add(new SessionVectorMatch(kvp.Key, sim));
            }
        }

        results.Sort(static (a, b) => b.Similarity.CompareTo(a.Similarity));
        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        return Task.FromResult<IReadOnlyList<SessionVectorMatch>>(results);
    }

    /// <inheritdoc/>
    public Task AddAsync(float[] vector, string signature, bool isBot, double botProbability,
        float[]? velocityVector = null,
        float[]? frequencyFingerprint = null,
        float[]? driftVector = null)
    {
        if (vector.Length != ExpectedDimensions)
        {
            _logger.LogDebug(
                "Skipping session vector with wrong dimension {Actual} (expected {Expected})",
                vector.Length, ExpectedDimensions);
            return Task.CompletedTask;
        }

        if (!IsValidVector(vector))
        {
            _logger.LogDebug("Skipping zero-norm session vector for {Signature}", signature);
            return Task.CompletedTask;
        }

        var meta = new SessionVectorMetadata
        {
            Signature = signature,
            IsBot = isBot,
            BotProbability = botProbability,
            Timestamp = DateTimeOffset.UtcNow,
            VelocityVector = velocityVector,
            VelocityMagnitude = velocityVector != null
                ? SessionVectorizer.VelocityMagnitude(velocityVector)
                : 0f,
            FrequencyFingerprint = frequencyFingerprint,
            DriftVector = driftVector
        };

        // Write to hot cache immediately
        _cache.Set(signature, new CacheEntry(vector, isBot, botProbability, meta), isBot: isBot);

        // LFU-sampled synchronous enqueue: borderline or high-threat sessions are worth
        // persisting; certain-and-harmless sessions are shed first. Non-blocking: no Task.Run.
        var row = new SessionCentroidRow
        {
            SignatureId = signature,
            Vector = vector,
            VelocityVector = velocityVector,
            FreqFingerprint = frequencyFingerprint,
            IsBot = isBot,
            BotProbability = botProbability
        };
        var necessity = DecisionNecessity.Value(
            botProbability: botProbability,
            threat: isBot ? botProbability : 0.0,
            ageSeconds: 0,
            threshold: _centroidOpts.DecisionThreshold,
            halfLifeSeconds: _centroidOpts.DecisionHalfLifeSeconds);
        if (necessity >= _centroidOpts.SamplingThreshold)
            _centroidWriter.Enqueue(new CentroidWriteMessage.SessionCentroidWrite(row));

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SessionVectorMatch>> FindSimilarMahalanobisAsync(
        float[] vector, int topK = 10, float maxDistance = 5.0f)
    {
        var scored = new List<(float Distance, string Signature)>();

        foreach (var kvp in _cache.GetAll())
        {
            var entry = kvp.Value;
            float dist;
            if (entry.Metadata.CompressionLevel >= 1 && entry.Metadata.VarianceVector is { Length: > 0 })
            {
                dist = SessionVectorizer.MahalanobisDistance(vector, entry.Vector, entry.Metadata.VarianceVector);
            }
            else
            {
                dist = 1f - CosineSimilarity(vector, entry.Vector);
                dist *= 3f; // scale-match to Mahalanobis range
            }

            if (dist <= maxDistance)
                scored.Add((dist, kvp.Key));
        }

        scored.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        if (scored.Count > topK)
            scored.RemoveRange(topK, scored.Count - topK);

        var results = scored
            .Select(x => new SessionVectorMatch(x.Signature, Math.Max(0f, 1f - x.Distance / (maxDistance + 1f))))
            .ToList();

        return Task.FromResult<IReadOnlyList<SessionVectorMatch>>(results);
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<GhostCentroidMatch>> FindGhostCentroidsAsync(
        float[] vector, int topK = 5, float minSimilarity = 0.75f)
    {
        var results = new List<(float Sim, GhostCentroidMatch Match)>();

        foreach (var kvp in _cache.GetAll())
        {
            var entry = kvp.Value;
            if (entry.Metadata.CompressionLevel < 1) continue;
            var sim = CosineSimilarity(vector, entry.Vector);
            if (sim < minSimilarity) continue;
            results.Add((sim, new GhostCentroidMatch(
                FamilyId: kvp.Key,
                Similarity: sim,
                CompressionLevel: entry.Metadata.CompressionLevel,
                IsBot: entry.IsBot,
                BotProbability: entry.BotProbability,
                VelocityVector: entry.Metadata.VelocityVector,
                VarianceVector: entry.Metadata.VarianceVector,
                FrequencyFingerprint: entry.Metadata.FrequencyFingerprint)));
        }

        results.Sort(static (a, b) => b.Sim.CompareTo(a.Sim));
        if (results.Count > topK)
            results.RemoveRange(topK, results.Count - topK);

        return Task.FromResult<IReadOnlyList<GhostCentroidMatch>>(
            results.Select(r => r.Match).ToList());
    }

    /// <inheritdoc/>
    /// <remarks>No-op: persistence is handled by SQLite, not JSON/HNSW files.</remarks>
    public Task SaveAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    /// <remarks>No-op: startup warm-up is performed by loading from SQLite via the centroid store.</remarks>
    public Task LoadAsync() => Task.CompletedTask;

    /// <inheritdoc/>
    public IReadOnlyList<(float[] Vector, SessionVectorMetadata Metadata)> GetAllVectorsSnapshot()
    {
        return _cache.GetAll()
            .Select(kvp => (kvp.Value.Vector, kvp.Value.Metadata))
            .ToList();
    }

    /// <inheritdoc/>
    public Task ReplaceAllAsync(IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
    {
        _cache.Clear();
        foreach (var (vec, meta) in items)
            _cache.Set(meta.Signature, new CacheEntry(vec, meta.IsBot, meta.BotProbability, meta), isBot: meta.IsBot);

        _logger.LogInformation("SlimSessionVectorSearch cache replaced with {Count} compacted vectors", items.Count);
        return Task.CompletedTask;
    }

    private static bool IsValidVector(float[] v) => VectorMath.IsValidVector(v);
    private static float CosineSimilarity(float[] a, float[] b) => VectorMath.CosineSimilarity(a, b);
}
