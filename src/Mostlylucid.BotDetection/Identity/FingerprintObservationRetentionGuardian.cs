using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Guardians;
using Mostlylucid.BotDetection.Identity.BrowserModes;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Identity data guardian that bounds the two absorbed-observation tables in
///     <c>fingerprints.db</c>: <c>fingerprint_observations</c> (Part B / Task 9)
///     and <c>fingerprint_mode_observations</c>. Absorbed rows accumulate forever
///     otherwise, since both absorption paths only stamp <c>absorbed_at</c> and
///     never delete; under a UA-rotating flood these are the residual
///     unbounded-growth leak on the identity tier. A soak measured the mode table
///     at ~71% of the identity DB's growth (one row per resolved mode per request),
///     which is why both tables are pruned in one guardian run.
///
///     Per run the guardian keeps, per fingerprint, all unabsorbed observations
///     plus the most-recent-K absorbed ones and prunes the rest via
///     <see cref="IFingerprintStore.PruneAbsorbedObservationsAsync"/> and
///     <see cref="IFingerprintBrowserModeStore.PruneAbsorbedModeObservationsAsync"/>.
///
///     <b>Drift preservation.</b> Two drift readers scan absorbed
///     <c>fingerprint_observations</c> rows with no <c>absorbed_at</c> filter:
///     <see cref="IFingerprintStore.GetLatestObservationVectorAsync"/>
///     (id DESC LIMIT 1) and <see cref="IFingerprintStore.ListRecentObservationsForDriftAsync"/>
///     (per-archetype, capped at
///     <see cref="IdentityOptions.DriftMaxRowsPerArchetype"/>). So the
///     effective keep-count is <c>max(MaxObservationsPerFingerprint, DriftMaxRowsPerArchetype)</c>
///     -- a prune can never starve archetype drift. The mode-observations table
///     has no absorbed-row reader (its only reader filters <c>absorbed_at IS NULL</c>
///     and the matcher reads centroids from <c>fingerprint_modes</c>), so the same
///     keep-count applies purely as a diagnostic margin there.
///
///     Both the interval and the enabled flag are config-driven via
///     <c>BotDetection:Guardians:FingerprintObservationRetention:*</c>. Registered
///     only under <c>Identity:Enabled</c> (the store is dormant otherwise).
/// </summary>
public sealed class FingerprintObservationRetentionGuardian : IGuardian
{
    private readonly IFingerprintStore _store;
    private readonly IFingerprintBrowserModeStore _modeStore;
    private readonly int _effectiveKeep;
    private readonly ILogger<FingerprintObservationRetentionGuardian> _logger;

    /// <summary>Default cadence when config supplies no interval.</summary>
    private static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(30);

    public FingerprintObservationRetentionGuardian(
        IFingerprintStore store,
        IFingerprintBrowserModeStore modeStore,
        IOptions<BotDetectionOptions> options,
        IConfiguration config,
        ILogger<FingerprintObservationRetentionGuardian> logger)
    {
        _store = store;
        _modeStore = modeStore;
        _logger = logger;

        // Drift-recency guard: never keep fewer than the drift reader's per-archetype
        // cap, regardless of the configured retention, or a prune would starve
        // ListRecentObservationsForDriftAsync (which reads absorbed rows unfiltered).
        _effectiveKeep = Math.Max(
            options.Value.Identity.MaxObservationsPerFingerprint,
            options.Value.Identity.DriftMaxRowsPerArchetype);

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

            var prunedObs = await _store.PruneAbsorbedObservationsAsync(_effectiveKeep, ct);
            var prunedModes = await _modeStore.PruneAbsorbedModeObservationsAsync(_effectiveKeep, ct);
            var pruned = prunedObs + prunedModes;

            _logger.LogDebug(
                "FingerprintObservationRetention: pruned {Obs} observations + {Modes} mode observations (keep {Keep})",
                prunedObs, prunedModes, _effectiveKeep);

            return new GuardianReport
            {
                GuardianName = Name,
                Category = Category,
                Status = "pruned",
                DurationMs = sw.Elapsed.TotalMilliseconds,
                Details = pruned > 0
                    ? $"{prunedObs} observations + {prunedModes} mode observations pruned"
                    : null
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
