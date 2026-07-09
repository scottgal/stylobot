using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Services;

/// <summary>
///     Nightly behavioral compression job implementing dynamic resolution adjustment (LOD-style).
///
///     After guardian decomposition this service owns only Phase 5:
///     cross-signature cap enforcement via <see cref="DecisionNecessity"/> eviction.
///     Phases 1-4 run as independent <see cref="IGuardian"/> implementations on
///     their own intervals:
///     <list type="bullet">
///         <item><b>Phase 1</b> - <c>BucketRetentionGuardian</c>: bucket pruning.</item>
///         <item><b>Phase 2</b> - <c>SessionCompactionGuardian</c>: SQLite session compaction.</item>
///         <item><b>Phase 3</b> - <c>HnswCompactionGuardian</c>: HNSW index compaction.</item>
///         <item><b>Phase 4</b> - <c>CentroidRetentionGuardian</c>: centroid pruning.</item>
///     </list>
///
///     Priority formula: risk x recency_decay x bot_probability x entity_bonus.
///     High-risk bots, entity-mapped identities, and recent visitors retain L0 longest.
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
    private readonly ILogger<VectorCompactionService> _logger;
    // Cross-signature cap governor (memory-pressure-adaptive). Null when disabled
    // (RetentionOptions.MaxSignatures == 0).
    private readonly MemoryAdaptiveCap? _signatureCap;
    private readonly double _botThreshold;

    public VectorCompactionService(
        IDetectionArchive store,
        IOptions<BotDetectionOptions> options,
        ILogger<VectorCompactionService> logger)
    {
        _store = store;
        _retention = options.Value.Retention;
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

        // Phase 4 (CentroidRetentionGuardian): centroid pruning extracted to its own
        // guardian and runs on its own interval; no call here.

        _logger.LogInformation("Vector compaction complete in {Elapsed:g}", sw.Elapsed);
        return compacted;
    }

    // ===========================
    // Phase 5: Cross-signature cap enforcement (DecisionNecessity eviction)
    // ===========================

    /// <summary>
    ///     Last-resort bound: when distinct signatures exceed the memory-adaptive
    ///     cap, evict the lowest-value ones by <see cref="DecisionNecessity"/> --
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
    ///     eviction score. Unknown -> 0 (the score falls back to bot-probability).</summary>
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
}
