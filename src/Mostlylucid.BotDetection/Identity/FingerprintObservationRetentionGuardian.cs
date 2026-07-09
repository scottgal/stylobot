using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Identity data guardian that bounds <c>fingerprints.db</c>'s
///     <c>fingerprint_observations</c> table (Part B / Task 9). Absorbed
///     observations accumulate forever otherwise, since the absorption path only
///     stamps <c>absorbed_at</c> and never deletes; under a UA-rotating flood that
///     is the residual unbounded-growth leak on the identity tier.
///
///     Per run the guardian keeps, per fingerprint, all unabsorbed observations
///     plus the most-recent-K absorbed ones and prunes the rest via
///     <see cref="IFingerprintStore.PruneAbsorbedObservationsAsync"/>.
///
///     <b>Drift preservation.</b> Two drift readers scan absorbed rows with no
///     <c>absorbed_at</c> filter: <see cref="IFingerprintStore.GetLatestObservationVectorAsync"/>
///     (id DESC LIMIT 1) and <see cref="IFingerprintStore.ListRecentObservationsForDriftAsync"/>
///     (per-archetype, capped at
///     <see cref="IdentityWeightCalibrationService.DriftMaxRowsPerArchetype"/>). So the
///     effective keep-count is <c>max(MaxObservationsPerFingerprint, DriftMaxRowsPerArchetype)</c>
///     -- a prune can never starve archetype drift.
///
///     Both the interval and the enabled flag are config-driven via
///     <c>BotDetection:Guardians:FingerprintObservationRetention:*</c>. Registered
///     only under <c>Identity:Enabled</c> (the store is dormant otherwise).
/// </summary>
public sealed class FingerprintObservationRetentionGuardian : IGuardian
{
    private readonly IFingerprintStore _store;
    private readonly int _effectiveKeep;
    private readonly ILogger<FingerprintObservationRetentionGuardian> _logger;

    /// <summary>Default cadence when config supplies no interval.</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    public FingerprintObservationRetentionGuardian(
        IFingerprintStore store,
        IOptions<BotDetectionOptions> options,
        IConfiguration config,
        ILogger<FingerprintObservationRetentionGuardian> logger)
    {
        _store = store;
        _logger = logger;

        // Drift-recency guard: never keep fewer than the drift reader's per-archetype
        // cap, regardless of the configured retention, or a prune would starve
        // ListRecentObservationsForDriftAsync (which reads absorbed rows unfiltered).
        _effectiveKeep = Math.Max(
            options.Value.Identity.MaxObservationsPerFingerprint,
            IdentityWeightCalibrationService.DriftMaxRowsPerArchetype);

        var (enabled, interval) = GuardianConfig.Read(
            config, "FingerprintObservationRetention", DefaultInterval);

        Enabled = enabled;
        Interval = interval;
    }

    public string Name => "FingerprintObservationRetention";
    public GuardianCategory Category => GuardianCategory.Data;
    public TimeSpan Interval { get; }
    public bool Enabled { get; }

    public async Task<GuardianReport> GuardAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            ct.ThrowIfCancellationRequested();

            var pruned = await _store.PruneAbsorbedObservationsAsync(_effectiveKeep, ct);

            _logger.LogDebug(
                "FingerprintObservationRetention: pruned {Pruned} absorbed observations (keep {Keep})",
                pruned, _effectiveKeep);

            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "pruned",
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = pruned > 0 ? $"{pruned} absorbed observations pruned" : null
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "FingerprintObservationRetention: prune failed");
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
}
