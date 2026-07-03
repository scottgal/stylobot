using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Similarity;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Storage;

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
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> that
///         slept until the configured compaction-hour and then ran. Now
///         subscribes to <see cref="TickCadence.Tick1h"/> and runs only when
///         the current UTC hour matches
///         <see cref="RetentionOptions.CompactionHourUtc"/> AND we haven't
///         already run this UTC day. See
///         <c>feedback_no_background_services</c>.
///     </para>
/// </summary>
public sealed class VectorCompactionService : IGuardian
{
    private readonly ISessionStore _store;
    private readonly ISessionVectorSearch? _vectorSearch;
    private readonly RetentionOptions _retention;
    private readonly SelfMaintenanceOptions _selfMaintenance;
    private readonly ISignatureCentroidStore _signatureCentroidStore;
    private readonly ISessionCentroidStore _sessionCentroidStore;
    private readonly IIntentCentroidStore _intentCentroidStore;
    private readonly ILogger<VectorCompactionService> _logger;
    // Cross-signature cap governor (memory-pressure-adaptive). Null when disabled
    // (RetentionOptions.MaxSignatures == 0).
    private readonly MemoryAdaptiveCap? _signatureCap;
    private readonly double _botThreshold;

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
        // The canonical bot/human boundary (v8 rationalisation). DecisionNecessity
        // peaks its uncertainty term here, so a signature sitting right on the
        // decision line is the most valuable to keep and the last to be evicted.
        //
        // INTENTIONAL: this uses the global BotDetectionOptions.Classification.BotFloor,
        // not the per-request EffectiveThresholds. Compaction is a background
        // guardian; it walks the whole store across all domains and has no
        // per-request HttpContext to consult. Compaction ranking against a single
        // global boundary is the right default -- per-domain compaction would
        // require store-partitioning by domain, which is a separate architectural
        // change.
        _botThreshold = options.Value.Classification.BotFloor;
        _signatureCap = _retention.MaxSignatures > 0
            ? new MemoryAdaptiveCap(_retention.MaxSignatures, floor: _retention.MinSignatures)
            : null;
    }

    // ── IGuardian ────────────────────────────────────────────────────────────
    // Storage compaction is a data-category guardian. The GuardianService walker
    // drives GuardAsync on Interval instead of the old daily hour-gate, so the
    // store stays bounded in near-real-time.

    public string Name => "VectorCompaction";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval => _retention.CompactionInterval;

    public async Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        // Behavioural compaction (within-signature) first, then cap enforcement
        // (cross-signature eviction) only if still over the adaptive cap.
        var compacted = await RunCompactionAsync(ct);
        var evicted = await RunPhase5CapEnforcementAsync(ct);
        var status = evicted > 0 ? "evicted" : compacted > 0 ? "compacted" : "ok";
        var details = (compacted, evicted) switch
        {
            (0, 0) => (string?)null,
            (_, 0) => $"{compacted} signatures compacted",
            (0, _) => $"{evicted} signatures evicted",
            _      => $"{compacted} compacted, {evicted} evicted"
        };
        return new GuardianReport
        {
            GuardianName = Name,
            Category = Category,
            Status = status,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            Details = details
        };
    }

    /// <summary>
    ///     One full compaction pass. Returns the number of signatures whose
    ///     overflowing sessions were folded into their root (the primary bounding
    ///     metric). Internal so tests can drive it directly.
    /// </summary>
    internal async Task<int> RunCompactionAsync(CancellationToken ct)
    {
        _logger.LogInformation("Vector compaction started");
        var sw = System.Diagnostics.Stopwatch.StartNew();

        // Phase 1: Delete stale bucket rows (chart counters only)
        await RunPhase1BucketPruneAsync(ct);

        // Phase 2: Compact overflowing SQLite sessions into behavioral centroids
        var compacted = await RunPhase2SessionCompactionAsync(ct);

        // Phase 3: Compact HNSW index if it's grown too large
        if (_vectorSearch != null)
            await RunPhase3HnswCompactionAsync(ct);

        // Phase 4: Prune stale centroid rows from all three centroid tables
        await RunCentroidPruningAsync(ct);

        _logger.LogInformation("Vector compaction complete in {Elapsed:g}", sw.Elapsed);
        return compacted;
    }

    // ===========================
    // Phase 5: Cross-signature cap enforcement (DecisionNecessity eviction)
    // ===========================

    /// <summary>
    ///     Last-resort bound: when distinct signatures exceed the memory-adaptive
    ///     cap, evict the lowest-value ones by <see cref="DecisionNecessity"/> —
    ///     resolved-and-harmless first, uncertain + risky retained. Engages only when
    ///     compaction + retention haven't kept the store under the cap (the rotation
    ///     case). Returns the number of signatures evicted.
    /// </summary>
    internal async Task<int> RunPhase5CapEnforcementAsync(CancellationToken ct)
    {
        if (_signatureCap is null) return 0; // disabled (MaxSignatures == 0)
        try
        {
            var effectiveMax = _signatureCap.Effective();
            var count = await _store.GetSignatureCountAsync(ct);
            var overflow = count - effectiveMax;
            if (overflow <= 0)
            {
                _logger.LogDebug("Phase 5: {Count} signatures within cap {Cap}", count, effectiveMax);
                return 0;
            }

            // Candidate pool: the oldest 2x-overflow (+buffer) signatures. The oldest
            // set is a coarse pre-filter; DecisionNecessity is the real prioritizer that
            // picks the lowest-value among them to evict (keep uncertain + risky).
            var candidateLimit = (int)Math.Min(count, (long)overflow * 2 + 100);
            var candidates = await _store.GetAllSignaturePriorityInfoAsync(candidateLimit, ct);
            if (candidates.Count == 0) return 0;

            var now = DateTime.UtcNow;
            var halfLife = _retention.SignatureRecencyHalfLife.TotalSeconds;
            var victims = candidates
                .OrderBy(s => DecisionNecessity.ColdnessScore(
                    s.BotProbability,
                    Math.Max(s.BotProbability, RiskBandToRisk(s.RiskBand)),
                    Math.Max(0, (now - s.LastSeen).TotalSeconds),
                    _botThreshold,
                    halfLife))
                .Take(overflow)
                .Select(s => s.Signature)
                .ToList();

            var evicted = await _store.DeleteSignaturesAsync(victims, ct);
            _logger.LogInformation(
                "Phase 5: evicted {Evicted} low-value signatures ({Count} over cap {Cap})",
                evicted, count, effectiveMax);
            return evicted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phase 5 (cap enforcement) failed");
            return 0;
        }
    }

    /// <summary>Maps a stored RiskBand string to a threat weight in [0,1] for the
    ///     eviction score. Unknown → 0 (the score falls back to bot-probability).</summary>
    private static double RiskBandToRisk(string? riskBand) => riskBand?.ToLowerInvariant() switch
    {
        "verylow"  => 0.05,
        "low"      => 0.15,
        "elevated" => 0.50,
        "medium"   => 0.50,
        "high"     => 0.85,
        "veryhigh" => 1.00,
        "verified" => 0.90,
        _          => 0.0
    };

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

    private async Task<int> RunPhase2SessionCompactionAsync(CancellationToken ct)
    {
        try
        {
            var overflowing = await _store.GetOverflowingSignaturesAsync(
                _retention.MaxSessionsPerSignature, limit: 1000, ct);

            if (overflowing.Count == 0)
            {
                _logger.LogDebug("Phase 2: no signatures over session limit ({Max})", _retention.MaxSessionsPerSignature);
                return 0;
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
            return compacted;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Phase 2 (session compaction) failed");
            return 0;
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
