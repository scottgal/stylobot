using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Similarity;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Nightly behavioral compression job implementing dynamic resolution adjustment (LOD-style).
///
///     Three-phase compaction:
///     Phase 1 - Bucket pruning: deletes time-series bucket rows older than BucketRetention.
///               Buckets are the only data type that is truly deleted.
///
///     Phase 2 - SQLite session compaction: for signatures exceeding MaxSessionsPerSignature,
///               computes a maturity-weighted behavioral centroid AND a velocity centroid
///               (average drift direction across consecutive sessions), stores as root_vector,
///               and deletes the old rows. Full-resolution sessions are preserved for the
///               most recent MaxSessionsPerSignature sessions per signature.
///
///     Phase 3 - HNSW index compaction: if total vector count exceeds threshold:
///               L1: collapse multiple same-signature vectors to one centroid entry (priority-ordered)
///               L2: if still above HnswLevel2Threshold, collapse low-priority clusters to
///                   a single cluster-centroid entry.
///
///     Priority formula: risk × recency_decay × bot_probability × entity_bonus.
///     High-risk bots, entity-mapped identities, and recent visitors retain L0 longest.
///     The velocity centroid is preserved through all compaction levels so downstream
///     analysis can see not just "what this client looks like" but "how it was changing."
/// </summary>
public sealed class VectorCompactionService : BackgroundService
{
    private readonly ISessionStore _store;
    private readonly ISessionVectorSearch? _vectorSearch;
    private readonly RetentionOptions _retention;
    private readonly SelfMaintenanceOptions _selfMaintenance;
    private readonly ISignatureCentroidStore _signatureCentroidStore;
    private readonly ISessionCentroidStore _sessionCentroidStore;
    private readonly IIntentCentroidStore _intentCentroidStore;
    private readonly ILogger<VectorCompactionService> _logger;

    public VectorCompactionService(
        ISessionStore store,
        IOptions<BotDetectionOptions> options,
        ILogger<VectorCompactionService> logger,
        ISignatureCentroidStore signatureCentroidStore,
        ISessionCentroidStore sessionCentroidStore,
        IIntentCentroidStore intentCentroidStore,
        ISessionVectorSearch? vectorSearch = null)
    {
        _store = store;
        _vectorSearch = vectorSearch;
        _retention = options.Value.Retention;
        _selfMaintenance = options.Value.SelfMaintenance;
        _signatureCentroidStore = signatureCentroidStore;
        _sessionCentroidStore = sessionCentroidStore;
        _intentCentroidStore = intentCentroidStore;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Delay initial run until configured hour today (or wait until tomorrow if past)
        try { await WaitForCompactionWindowAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunCompactionAsync(stoppingToken);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Vector compaction cycle failed — will retry next scheduled window");
            }

            // Wait for the next nightly window
            try { await WaitForCompactionWindowAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    private async Task WaitForCompactionWindowAsync(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        var nextRun = new DateTime(now.Year, now.Month, now.Day, _retention.CompactionHourUtc, 0, 0, DateTimeKind.Utc);
        if (nextRun <= now) nextRun = nextRun.AddDays(1);

        var delay = nextRun - now;
        _logger.LogDebug("Vector compaction scheduled at {NextRun:O} (in {Delay:g})", nextRun, delay);
        await Task.Delay(delay, ct);
    }

    internal async Task RunCompactionAsync(CancellationToken ct)
    {
        _logger.LogInformation("Vector compaction started");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Phase 1: Delete stale bucket rows (chart counters only)
        await RunPhase1BucketPruneAsync(ct);

        // Phase 2: Compact overflowing SQLite sessions into behavioral centroids
        await RunPhase2SessionCompactionAsync(ct);

        // Phase 3: Compact HNSW index if it's grown too large
        if (_vectorSearch != null)
            await RunPhase3HnswCompactionAsync(ct);

        // Phase 4: Prune stale centroid rows from all three centroid tables
        await RunCentroidPruningAsync(ct);

        _logger.LogInformation("Vector compaction complete in {Elapsed:g}", sw.Elapsed);
    }

    // ===========================
    // Phase 1: Bucket pruning
    // ===========================

    private async Task RunPhase1BucketPruneAsync(CancellationToken ct)
    {
        try
        {
            await _store.PruneBucketsAsync(_retention.BucketRetention, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phase 1 (bucket pruning) failed");
        }
    }

    // ===========================
    // Phase 4: Centroid pruning
    // ===========================

    internal async Task RunCentroidPruningAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow
            .AddDays(-_selfMaintenance.CentroidRetentionDays)
            .ToUnixTimeSeconds();

        try
        {
            await Task.WhenAll(
                _signatureCentroidStore.PruneSignaturesOlderThanAsync(cutoff, ct),
                _sessionCentroidStore.PruneSessionsOlderThanAsync(cutoff, ct),
                _intentCentroidStore.PruneIntentsOlderThanAsync(cutoff, ct));

            _logger.LogDebug(
                "Phase 4: pruned centroid rows older than {CutoffEpoch} (retention={Days}d)",
                cutoff, _selfMaintenance.CentroidRetentionDays);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phase 4 (centroid pruning) failed");
        }
    }

    // ===========================
    // Phase 2: SQLite session compaction
    // ===========================

    private async Task RunPhase2SessionCompactionAsync(CancellationToken ct)
    {
        try
        {
            var overflowing = await _store.GetOverflowingSignaturesAsync(
                _retention.MaxSessionsPerSignature, limit: 1000, ct);

            if (overflowing.Count == 0)
            {
                _logger.LogDebug("Phase 2: no signatures over session limit ({Max})", _retention.MaxSessionsPerSignature);
                return;
            }

            _logger.LogInformation("Phase 2: compacting {Count} signatures over {Max}-session limit",
                overflowing.Count, _retention.MaxSessionsPerSignature);

            var compacted = 0;
            foreach (var (signature, sessionCount) in overflowing)
            {
                if (ct.IsCancellationRequested) break;

                var result = await _store.CompactSignatureSessionsAsync(
                    signature, _retention.MaxSessionsPerSignature, ct);

                if (result.HasCentroid && _vectorSearch != null)
                {
                    // Update HNSW metadata: replace individual vectors for this signature
                    // with a single centroid entry carrying the velocity centroid
                    await UpdateHnswEntryForSignatureAsync(result, ct);
                }

                if (result.CompactedCount > 0) compacted++;
            }

            _logger.LogInformation("Phase 2 complete: {Count} signatures compacted", compacted);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phase 2 (session compaction) failed");
        }
    }

    private async Task UpdateHnswEntryForSignatureAsync(CompactionResult result, CancellationToken ct)
    {
        if (_vectorSearch == null || result.BehavioralCentroid == null) return;

        var all = _vectorSearch.GetAllVectorsSnapshot();
        var remaining = all
            .Where(x => x.Metadata.Signature != result.Signature)
            .ToList();

        // Add the compacted centroid entry with velocity centroid embedded in metadata
        var centroidMeta = new SessionVectorMetadata
        {
            Signature = result.Signature,
            IsBot = false, // Will be updated by next live session; we don't know from the centroid alone
            BotProbability = 0,
            Timestamp = DateTimeOffset.UtcNow,
            VelocityVector = result.VelocityCentroid,
            VelocityMagnitude = result.VelocityCentroid != null
                ? Analysis.SessionVectorizer.VelocityMagnitude(result.VelocityCentroid)
                : 0f,
            FrequencyFingerprint = result.FrequencyCentroid,
            CompressionLevel = 1, // L1 centroid
            Priority = 1.0
        };

        remaining.Add((result.BehavioralCentroid, centroidMeta));
        await _vectorSearch.ReplaceAllAsync(remaining);
    }

    // ===========================
    // Phase 3: HNSW compaction
    // ===========================

    private async Task RunPhase3HnswCompactionAsync(CancellationToken ct)
    {
        if (_vectorSearch == null) return;

        var count = _vectorSearch.Count;
        if (count <= _retention.HnswLevel1Threshold)
        {
            _logger.LogDebug("Phase 3: HNSW index has {Count} vectors (below L1 threshold {L1}), skipping",
                count, _retention.HnswLevel1Threshold);
            return;
        }

        _logger.LogInformation("Phase 3: HNSW index has {Count} vectors (L1 threshold={L1}, L2={L2})",
            count, _retention.HnswLevel1Threshold, _retention.HnswLevel2Threshold);

        var all = _vectorSearch.GetAllVectorsSnapshot();

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
                    ClusterId = items.FirstOrDefault(x => x.Metadata.ClusterId != null).Metadata.ClusterId
                };
                compacted.Add((centroid, meta));
                currentCount++;
            }
        }

        // L2: if still over threshold, merge low-priority signatures in the same cluster
        if (currentCount > _retention.HnswLevel2Threshold)
        {
            _logger.LogInformation(
                "Phase 3 L2: still {Count} vectors after L1, applying cluster-level compaction",
                currentCount);

            var l2Result = ApplyL2ClusterCompaction(compacted, priorityMap);
            await _vectorSearch.ReplaceAllAsync(l2Result);

            _logger.LogInformation("Phase 3 complete: {Before} → {After} vectors", count, l2Result.Count);
        }
        else
        {
            await _vectorSearch.ReplaceAllAsync(compacted);
            _logger.LogInformation("Phase 3 L1 complete: {Before} → {After} vectors", count, compacted.Count);
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
