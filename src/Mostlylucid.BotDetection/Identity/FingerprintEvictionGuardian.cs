using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Storage;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Identity data guardian that enforces the cross-fingerprint memory cap on
///     <c>fingerprints.db</c> via <see cref="DecisionNecessity"/> eviction (Part B /
///     Task 10). Mirrors <c>SignatureCapGuardian</c> on the signature side: when the
///     number of distinct fingerprints exceeds the
///     <see cref="MemoryAdaptiveCap"/>-adjusted ceiling derived from
///     <see cref="IdentityOptions.MaxFingerprints"/>, the guardian evicts the
///     lowest-value ones first (resolved-and-harmless before uncertain or risky).
///
///     Eviction cascade-deletes every per-fingerprint table via
///     <see cref="IFingerprintStore.DeleteFingerprintsAsync"/>.
///
///     <b>Protection.</b> <c>claim_status = 'verified'</c> fingerprints are filtered
///     out of the candidate set and never evicted. If the protected set alone
///     exceeds the cap, the guardian holds (evicts nothing) and logs.
///
///     Disabled (returns <c>Status = "ok"</c>) when
///     <see cref="IdentityOptions.MaxFingerprints"/> is 0 (the unlimited posture).
///     Both the interval and the enabled flag are config-driven via
///     <c>BotDetection:Guardians:FingerprintEviction:*</c>. Registered only under
///     <c>Identity:Enabled</c>.
/// </summary>
public sealed class FingerprintEvictionGuardian : IGuardian
{
    private readonly IFingerprintStore _store;
    private readonly double _botThreshold;
    private readonly double _recencyHalfLifeSeconds;
    // Cross-fingerprint cap governor (memory-pressure-adaptive). Null when disabled
    // (IdentityOptions.MaxFingerprints == 0).
    private readonly MemoryAdaptiveCap? _cap;
    private readonly ILogger<FingerprintEvictionGuardian> _logger;

    /// <summary>Default cadence when config supplies no interval.</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    public FingerprintEvictionGuardian(
        IFingerprintStore store,
        IOptions<BotDetectionOptions> options,
        IConfiguration config,
        ILogger<FingerprintEvictionGuardian> logger)
    {
        _store = store;
        _logger = logger;

        // The canonical bot/human boundary (v8 rationalisation): DecisionNecessity
        // peaks its uncertainty term here, so a fingerprint sitting right on the
        // decision line is the most valuable to keep and the last to be evicted.
        _botThreshold = options.Value.Classification.BotFloor;

        var identity = options.Value.Identity;
        _recencyHalfLifeSeconds = identity.FingerprintRecencyHalfLife.TotalSeconds;
        _cap = identity.MaxFingerprints > 0
            ? new MemoryAdaptiveCap(identity.MaxFingerprints, floor: identity.MinFingerprints)
            : null;

        var (enabled, interval) = GuardianConfig.Read(
            config, "FingerprintEviction", DefaultInterval);

        Enabled = enabled;
        Interval = interval;
    }

    public string Name => "FingerprintEviction";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval { get; }
    public bool Enabled { get; }

    public async Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var evicted = await RunCapEnforcementAsync(ct);
        var status = evicted > 0 ? "evicted" : "ok";
        var details = evicted > 0 ? $"{evicted} fingerprints evicted" : null;
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
    ///     When distinct fingerprints exceed the memory-adaptive cap, evict the
    ///     lowest-value non-protected ones by <see cref="DecisionNecessity"/>.
    ///     Returns the number of fingerprints evicted.
    /// </summary>
    internal async Task<int> RunCapEnforcementAsync(CancellationToken ct)
    {
        if (_cap is null) return 0; // disabled (MaxFingerprints == 0)

        try
        {
            var effectiveMax = _cap.Effective();
            var count = await _store.GetFingerprintCountAsync(ct);
            var overflow = count - effectiveMax;

            if (overflow <= 0)
            {
                _logger.LogDebug(
                    "FingerprintEviction: {Count} fingerprints within cap {Cap}", count, effectiveMax);
                return 0;
            }

            // Candidate pool: the oldest 2x-overflow (+buffer) fingerprints. The oldest
            // set is a coarse pre-filter; DecisionNecessity picks the lowest-value among
            // them and verified claims are removed before ranking.
            var candidateLimit = (int)Math.Min(count, (long)overflow * 2 + 100);
            var candidates = await _store.GetAllFingerprintPriorityInfoAsync(candidateLimit, ct);

            var evictable = candidates.Where(c => !c.Protected).ToList();
            if (evictable.Count == 0)
            {
                _logger.LogInformation(
                    "FingerprintEviction: {Count} fingerprints over cap {Cap} but all candidates are " +
                    "verified/protected -- holding", count, effectiveMax);
                return 0;
            }

            var now = DateTime.UtcNow;
            var victims = evictable
                .OrderBy(p => DecisionNecessity.ColdnessScore(
                    p.BotProbability,
                    Math.Max(p.BotProbability, GuardianRisk.RiskBandToRisk(p.RiskBand)),
                    Math.Max(0, (now - p.LastSeen).TotalSeconds),
                    _botThreshold,
                    _recencyHalfLifeSeconds))
                .Take(overflow)
                .Select(p => p.FingerprintId)
                .ToList();

            var evicted = await _store.DeleteFingerprintsAsync(victims, ct);
            _logger.LogInformation(
                "FingerprintEviction: evicted {Evicted} low-value fingerprints ({Count} over cap {Cap})",
                evicted, count, effectiveMax);
            return evicted;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FingerprintEviction: cap enforcement failed");
            return 0;
        }
    }
}
