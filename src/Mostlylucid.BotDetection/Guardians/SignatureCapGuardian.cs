using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Data;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Guardians;

/// <summary>
///     Data guardian that enforces the cross-signature memory cap via
///     <see cref="DecisionNecessity"/> eviction (Phase 5 of the old
///     VectorCompactionService).
///
///     When the number of distinct signatures exceeds the
///     <see cref="MemoryAdaptiveCap"/>-adjusted ceiling derived from
///     <see cref="RetentionOptions.MaxSignatures"/>, the guardian pulls the
///     oldest candidate pool and evicts the lowest-value entries first:
///     resolved-and-harmless (cold) signatures go before uncertain or risky ones.
///
///     The cap is disabled when <see cref="RetentionOptions.MaxSignatures"/> is 0
///     (the unlimited posture); <see cref="GuardAsync"/> returns
///     <c>Status = "ok"</c> immediately in that case.
///
///     Both the interval and the enabled flag are config-driven via
///     <c>BotDetection:Guardians:SignatureCap:*</c>.
/// </summary>
public sealed class SignatureCapGuardian : IGuardian
{
    private readonly IDetectionArchive _store;
    private readonly RetentionOptions _retention;
    private readonly double _botThreshold;
    // Cross-signature cap governor (memory-pressure-adaptive). Null when disabled
    // (RetentionOptions.MaxSignatures == 0).
    private readonly MemoryAdaptiveCap? _cap;
    private readonly ILogger<SignatureCapGuardian> _logger;

    public SignatureCapGuardian(
        IDetectionArchive store,
        IOptions<BotDetectionOptions> options,
        IConfiguration config,
        ILogger<SignatureCapGuardian> logger)
    {
        _store = store;
        _retention = options.Value.Retention;
        _logger = logger;

        // The canonical bot/human boundary (v8 rationalisation). DecisionNecessity
        // peaks its uncertainty term here, so a signature sitting right on the
        // decision line is the most valuable to keep and the last to be evicted.
        //
        // INTENTIONAL: this uses the global BotDetectionOptions.Classification.BotFloor,
        // not per-request EffectiveThresholds. Compaction is a background guardian;
        // it walks the whole store across all domains and has no per-request HttpContext
        // to consult. Ranking against a single global boundary is the right default --
        // per-domain compaction would require store-partitioning by domain, which is a
        // separate architectural change.
        _botThreshold = options.Value.Classification.BotFloor;

        _cap = _retention.MaxSignatures > 0
            ? new MemoryAdaptiveCap(_retention.MaxSignatures, floor: _retention.MinSignatures)
            : null;

        var (enabled, interval) = GuardianConfig.Read(
            config, "SignatureCap", _retention.CompactionInterval);

        Enabled = enabled;
        Interval = interval;
    }

    // ── IGuardian ────────────────────────────────────────────────────────────

    public string Name => "SignatureCap";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval { get; }
    public bool Enabled { get; }

    public async Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var evicted = await RunCapEnforcementAsync(ct);
        var status = evicted > 0 ? "evicted" : "ok";
        var details = evicted > 0 ? $"{evicted} signatures evicted" : null;
        return new GuardianReport
        {
            GuardianName = Name,
            Category = Category,
            Status = status,
            DurationMs = sw.Elapsed.TotalMilliseconds,
            Details = details
        };
    }

    // ============================================================
    // Implementation
    // ============================================================

    /// <summary>
    ///     Last-resort bound: when distinct signatures exceed the memory-adaptive
    ///     cap, evict the lowest-value ones by <see cref="DecisionNecessity"/> --
    ///     resolved-and-harmless first, uncertain + risky retained. Engages only
    ///     when compaction + retention haven't kept the store under the cap (the
    ///     rotation case). Returns the number of signatures evicted.
    /// </summary>
    internal async Task<int> RunCapEnforcementAsync(CancellationToken ct)
    {
        if (_cap is null) return 0; // disabled (MaxSignatures == 0)

        try
        {
            var effectiveMax = _cap.Effective();
            var count = await _store.GetSignatureCountAsync(ct);
            var overflow = count - effectiveMax;

            if (overflow <= 0)
            {
                _logger.LogDebug(
                    "SignatureCap: {Count} signatures within cap {Cap}", count, effectiveMax);
                return 0;
            }

            // Candidate pool: the oldest 2x-overflow (+buffer) signatures. The oldest
            // set is a coarse pre-filter; DecisionNecessity is the real prioritiser
            // that picks the lowest-value among them (keep uncertain + risky).
            var candidateLimit = (int)Math.Min(count, (long)overflow * 2 + 100);
            var candidates = await _store.GetAllSignaturePriorityInfoAsync(candidateLimit, ct);
            if (candidates.Count == 0) return 0;

            var now = DateTime.UtcNow;
            var halfLife = _retention.SignatureRecencyHalfLife.TotalSeconds;
            var victims = candidates
                .OrderBy(s => DecisionNecessity.ColdnessScore(
                    s.BotProbability,
                    Math.Max(s.BotProbability, GuardianRisk.RiskBandToRisk(s.RiskBand)),
                    Math.Max(0, (now - s.LastSeen).TotalSeconds),
                    _botThreshold,
                    halfLife))
                .Take(overflow)
                .Select(s => s.Signature)
                .ToList();

            var evicted = await _store.DeleteSignaturesAsync(victims, ct);
            _logger.LogInformation(
                "SignatureCap: evicted {Evicted} low-value signatures ({Count} over cap {Cap})",
                evicted, count, effectiveMax);
            return evicted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SignatureCap: cap enforcement failed");
            return 0;
        }
    }
}
