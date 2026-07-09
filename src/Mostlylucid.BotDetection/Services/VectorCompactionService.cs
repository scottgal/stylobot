using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Data.Contracts;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
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
    private readonly IDetectionArchive _store;
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
        IDetectionArchive store,
        IOptions<BotDetectionOptions> options,
        ILogger<VectorCompactionService> logger,
        ISignatureCentroidStore signatureCentroidStore,
        ISessionCentroidStore sessionCentroidStore,
        IIntentCentroidStore intentCentroidStore)
    {
        _store = store;
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

        // Phase 1 (BucketRetentionGuardian): bucket pruning extracted to its own
        // guardian and runs on its own interval; no call here.

        // Phase 2 (SessionCompactionGuardian): SQLite session compaction extracted
        // to its own guardian and runs on its own interval; no call here.
        var compacted = 0;

        // Phase 3 (HnswCompactionGuardian): HNSW index compaction extracted to its own
        // guardian and runs on its own interval; no call here.

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

}
