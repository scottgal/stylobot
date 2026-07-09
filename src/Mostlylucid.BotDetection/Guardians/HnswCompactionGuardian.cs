using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Data guardian that compacts the HNSW session vector index when it grows beyond
///     threshold (Phase 3 of the old VectorCompactionService).
///
///     Two compaction levels:
///     <list type="bullet">
///         <item><b>L1</b> — collapses multiple same-signature vectors to a single
///             per-signature centroid entry (priority-ordered; lowest priority compressed
///             first). Runs when <see cref="ISessionVectorSearch.Count"/> exceeds
///             <see cref="RetentionOptions.HnswLevel1Threshold"/>.</item>
///         <item><b>L2</b> — if still above <see cref="RetentionOptions.HnswLevel2Threshold"/>
///             after L1, collapses low-priority signatures in the same cluster to a
///             single cluster-centroid entry. Low-priority is defined by
///             <see cref="RetentionOptions.L2CompactionPriorityThreshold"/>.</item>
///     </list>
///
///     When <see cref="ISessionVectorSearch"/> is null (commercial HNSW not registered),
///     the guardian returns a no-op <see cref="GuardianReport.Ok"/> immediately.
///
///     This is a behaviour-preserving extract: the body is the exact
///     <c>RunPhase3HnswCompactionAsync</c> + <c>ApplyL2ClusterCompaction</c> +
///     centroid helper logic from VectorCompactionService, wrapped in the
///     <see cref="IGuardian"/> contract. Both the interval and the enabled flag are
///     config-driven via <c>BotDetection:Guardians:HnswCompaction:*</c>.
/// </summary>
public sealed class HnswCompactionGuardian : IGuardian
{
    private readonly IDetectionArchive _store;
    private readonly ISessionVectorSearch? _vectorSearch;
    private readonly RetentionOptions _retention;
    private readonly ILogger<HnswCompactionGuardian> _logger;

    public HnswCompactionGuardian(
        IDetectionArchive store,
        IOptions<BotDetectionOptions> options,
        IConfiguration config,
        ILogger<HnswCompactionGuardian> logger,
        ISessionVectorSearch? vectorSearch = null)
    {
        _store = store;
        _vectorSearch = vectorSearch;
        _retention = options.Value.Retention;
        _logger = logger;

        var (enabled, interval) = GuardianConfig.Read(
            config, "HnswCompaction", options.Value.Retention.CompactionInterval);

        Enabled = enabled;
        Interval = interval;
    }

    public string Name => "HnswCompaction";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval { get; }
    public bool Enabled { get; }

    public async Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        if (_vectorSearch == null)
            return GuardianReport.Ok(this, sw.Elapsed.TotalMilliseconds);

        try
        {
            ct.ThrowIfCancellationRequested();

            var countBefore = _vectorSearch.Count;
            if (countBefore <= _retention.HnswLevel1Threshold)
            {
                _logger.LogDebug(
                    "HnswCompaction: {Count} vectors (below L1 threshold {L1}), skipping",
                    countBefore, _retention.HnswLevel1Threshold);
                return GuardianReport.Ok(this, sw.Elapsed.TotalMilliseconds, countBefore);
            }

            await RunHnswCompactionAsync(countBefore, ct);

            var countAfter = _vectorSearch.Count;
            var status = countAfter < countBefore ? "compacted" : "ok";
            var details = countAfter < countBefore
                ? $"{countBefore} → {countAfter} vectors"
                : null;

            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = status,
                RowsBefore = countBefore,
                RowsAfter = countAfter,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = details
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "HnswCompaction: compaction run failed");
            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "error",
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = ex.Message
            };
        }
    }

    // ===========================
    // Phase 3: HNSW compaction (moved from VectorCompactionService)
    // ===========================

    private async Task RunHnswCompactionAsync(int count, CancellationToken ct)
    {
        _logger.LogInformation(
            "HnswCompaction: {Count} vectors (L1 threshold={L1}, L2={L2})",
            count, _retention.HnswLevel1Threshold, _retention.HnswLevel2Threshold);

        var all = _vectorSearch!.GetAllVectorsSnapshot();

        // Get priority info for all unique signatures
        var signatures = all.Select(x => x.Metadata.Signature).Distinct().ToList();
        var priorityInfo = await _store.GetSignaturePriorityInfoAsync(signatures, ct);
        var priorityMap = priorityInfo.ToDictionary(p => p.Signature, p => p.Priority);

        // Group by signature; sort by priority ascending (lowest priority compressed first)
        var bySignature = all
            .GroupBy(x => x.Metadata.Signature)
            .OrderBy(g => priorityMap.GetValueOrDefault(g.Key, 0.5))
            .ToList();

        var compacted = new List<(float[] Vector, SessionVectorMetadata Meta)>();
        var currentCount = 0;

        // L1: collapse multi-vector signatures to per-signature centroid
        foreach (var group in bySignature)
        {
            var items = group.ToList();
            if (items.Count == 1 || currentCount + items.Count <= _retention.HnswLevel1Threshold)
            {
                // Keep at full resolution (highest priority signatures)
                compacted.AddRange(items);
                currentCount += items.Count;
            }
            else
            {
                // Collapse to L1 centroid
                var centroid = ComputeBehavioralCentroid(items);
                var velCentroid = ComputeVelocityCentroid(items);
                var variance = ComputeVarianceVector(items);
                var freqCentroid = ComputeFrequencyFingerprintCentroid(items);
                var priority = priorityMap.GetValueOrDefault(group.Key, 0.5);

                var meta = new SessionVectorMetadata
                {
                    Signature = group.Key,
                    IsBot = items.Any(x => x.Metadata.IsBot),
                    BotProbability = items.Max(x => x.Metadata.BotProbability),
                    Timestamp = items.Max(x => x.Metadata.Timestamp),
                    VelocityVector = velCentroid,
                    VelocityMagnitude = velCentroid != null
                        ? Analysis.SessionVectorizer.VelocityMagnitude(velCentroid)
                        : 0f,
                    VarianceVector = variance,
                    FrequencyFingerprint = freqCentroid,
                    CompressionLevel = 1,
                    Priority = priority,
                    ClusterId = items.Select(x => x.Metadata.ClusterId).FirstOrDefault(id => id != null)
                };
                compacted.Add((centroid, meta));
                currentCount++;
            }
        }

        // L2: if still over threshold, merge low-priority signatures in the same cluster
        if (currentCount > _retention.HnswLevel2Threshold)
        {
            _logger.LogInformation(
                "HnswCompaction L2: still {Count} vectors after L1, applying cluster-level compaction",
                currentCount);

            var l2Result = ApplyL2ClusterCompaction(compacted, priorityMap);
            await _vectorSearch.ReplaceAllAsync(l2Result);

            _logger.LogInformation(
                "HnswCompaction complete: {Before} → {After} vectors", count, l2Result.Count);
        }
        else
        {
            await _vectorSearch.ReplaceAllAsync(compacted);
            _logger.LogInformation(
                "HnswCompaction L1 complete: {Before} → {After} vectors", count, compacted.Count);
        }
    }

    private List<(float[] Vector, SessionVectorMetadata Meta)> ApplyL2ClusterCompaction(
        List<(float[] Vector, SessionVectorMetadata Meta)> items,
        Dictionary<string, double> priorityMap)
    {
        // Group by cluster; signatures without a cluster ID are kept as-is
        var clustered = items
            .Where(x => x.Meta.ClusterId != null)
            .GroupBy(x => x.Meta.ClusterId!)
            .ToList();

        var unclustered = items.Where(x => x.Meta.ClusterId == null).ToList();
        var result = new List<(float[] Vector, SessionVectorMetadata Meta)>(unclustered);

        foreach (var cluster in clustered)
        {
            var clusterItems = cluster.ToList();
            // Sort by priority: keep high-priority at L1, collapse low-priority to L2 centroid
            var highPriority = clusterItems
                .Where(x => priorityMap.GetValueOrDefault(x.Meta.Signature, 0) > _retention.L2CompactionPriorityThreshold)
                .ToList();
            var lowPriority = clusterItems
                .Where(x => priorityMap.GetValueOrDefault(x.Meta.Signature, 0) <= _retention.L2CompactionPriorityThreshold)
                .ToList();

            result.AddRange(highPriority);

            if (lowPriority.Count > 1)
            {
                // Collapse all low-priority signatures in this cluster to a single cluster centroid
                var centroid = ComputeBehavioralCentroid(lowPriority);
                var velCentroid = ComputeVelocityCentroid(lowPriority);
                var variance = ComputeVarianceVector(lowPriority);
                var freqCentroid = ComputeFrequencyFingerprintCentroid(lowPriority);

                var meta = new SessionVectorMetadata
                {
                    Signature = $"cluster:{cluster.Key}",
                    IsBot = lowPriority.Any(x => x.Meta.IsBot),
                    BotProbability = lowPriority.Max(x => x.Meta.BotProbability),
                    Timestamp = lowPriority.Max(x => x.Meta.Timestamp),
                    VelocityVector = velCentroid,
                    VelocityMagnitude = velCentroid != null
                        ? Analysis.SessionVectorizer.VelocityMagnitude(velCentroid)
                        : 0f,
                    VarianceVector = variance,
                    FrequencyFingerprint = freqCentroid,
                    CompressionLevel = 2,
                    Priority = lowPriority.Average(x => priorityMap.GetValueOrDefault(x.Meta.Signature, 0)),
                    ClusterId = cluster.Key
                };
                result.Add((centroid, meta));
            }
            else
            {
                result.AddRange(lowPriority);
            }
        }

        return result;
    }

    private static float[] ComputeBehavioralCentroid(
        IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
    {
        if (items.Count == 0) return [];
        var dims = items[0].Vector.Length;
        var centroid = new float[dims];
        foreach (var (v, _) in items)
            for (var i = 0; i < dims && i < v.Length; i++)
                centroid[i] += v[i];
        for (var i = 0; i < dims; i++)
            centroid[i] /= items.Count;
        return centroid;
    }

    /// <summary>
    ///     Computes per-dimension variance for the given set of vectors.
    ///     Stored alongside the centroid in metadata to enable Mahalanobis distance
    ///     during ghost shape matching: low-variance dimensions are discriminative.
    /// </summary>
    private static float[]? ComputeVarianceVector(
        IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
    {
        if (items.Count < 2) return null;
        var vectors = items.Select(x => x.Vector).ToList();
        return Analysis.SessionVectorizer.ComputeVarianceVector(vectors);
    }

    /// <summary>
    ///     Computes the frequency fingerprint centroid: mean of all frequency fingerprints
    ///     in this group that have one. Represents the campaign's typical temporal rhythm.
    /// </summary>
    private static float[]? ComputeFrequencyFingerprintCentroid(
        IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
    {
        var withFp = items.Where(x => x.Meta.FrequencyFingerprint is { Length: > 0 }).ToList();
        if (withFp.Count == 0) return null;

        var dims = withFp[0].Meta.FrequencyFingerprint!.Length;
        var centroid = new float[dims];
        foreach (var (_, meta) in withFp)
            for (var i = 0; i < dims && i < meta.FrequencyFingerprint!.Length; i++)
                centroid[i] += meta.FrequencyFingerprint[i];
        for (var i = 0; i < dims; i++) centroid[i] /= withFp.Count;
        return centroid;
    }

    private static float[]? ComputeVelocityCentroid(
        IReadOnlyList<(float[] Vector, SessionVectorMetadata Meta)> items)
    {
        // Prefer stored velocity vectors in metadata (most accurate)
        var withVelocity = items.Where(x => x.Meta.VelocityVector is { Length: > 0 }).ToList();
        if (withVelocity.Count > 0)
        {
            var dims = withVelocity[0].Meta.VelocityVector!.Length;
            var centroid = new float[dims];
            foreach (var (_, meta) in withVelocity)
                for (var i = 0; i < dims && i < meta.VelocityVector!.Length; i++)
                    centroid[i] += meta.VelocityVector[i];
            for (var i = 0; i < dims; i++)
                centroid[i] /= withVelocity.Count;
            return centroid;
        }

        // Fallback: compute velocity from consecutive vectors in this group (ordered by timestamp)
        var ordered = items.OrderBy(x => x.Meta.Timestamp).ToList();
        if (ordered.Count < 2) return null;

        var vdims = ordered[0].Vector.Length;
        var velSum = new float[vdims];
        var count = 0;
        for (var i = 1; i < ordered.Count; i++)
        {
            var delta = Analysis.SessionVectorizer.ComputeVelocity(ordered[i].Vector, ordered[i - 1].Vector);
            for (var d = 0; d < vdims && d < delta.Length; d++)
                velSum[d] += delta[d];
            count++;
        }
        if (count == 0) return null;
        var result = new float[vdims];
        for (var d = 0; d < vdims; d++)
            result[d] = velSum[d] / count;
        return result;
    }
}
