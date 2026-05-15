using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Background absorption: folds detailed observations into fingerprint centroids using
///     maturity-weighted means, applies the per-fingerprint stability learning signal, and
///     bumps the fingerprint's centroid_maturity. Per the spec's feedback latency tier,
///     "per-fingerprint absorption fires per maturity threshold" — this implementation
///     ticks on a fixed cadence and absorbs everything that has met the threshold since the
///     last tick.
///
///     Inferred client type recompute and archetype refinement live in later slices; this
///     service is purely the centroid + stability path.
///
///     Dormant when <c>BotDetectionOptions.Identity.Enabled</c> is false.
/// </summary>
public sealed class FingerprintAbsorptionService : BackgroundService
{
    private readonly ILogger<FingerprintAbsorptionService> _logger;
    private readonly SqliteFingerprintStore _store;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;

    public FingerprintAbsorptionService(
        ILogger<FingerprintAbsorptionService> logger,
        SqliteFingerprintStore store,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("FingerprintAbsorptionService dormant: Identity.Enabled = false");
            return;
        }

        // Hot fingerprints fire absorption every few seconds (their maturity threshold of N
        // requests fills fast); cold fingerprints absorb when the age threshold trips. The
        // service tick interval just bounds how often the scan runs — work-availability
        // determines the actual absorption rate.
        var tick = TimeSpan.FromSeconds(Math.Max(1, _options.Drift.DriftCheckIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var absorbed = await TickOnceAsync(stoppingToken);
                if (absorbed > 0)
                    _logger.LogDebug("Absorption tick folded {Count} observations", absorbed);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Absorption tick failed");
            }

            try { await Task.Delay(tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over absorbable observations. Returns the count folded.</summary>
    public async Task<int> TickOnceAsync(CancellationToken ct)
    {
        var batch = await _store.ListAbsorbableObservationsAsync(
            _options.Vector.AbsorptionMaturityThreshold,
            _options.Vector.AbsorptionAgeDays,
            _options.Vector.ActiveWindowDays,
            ct);

        // Track in-flight per-fingerprint centroid + maturity so multiple absorptions in the
        // same tick fold sequentially against the latest values, not against the stale row.
        var inflight = new Dictionary<string, (float[] Centroid, int Maturity, float[] Weights)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var obs in batch)
        {
            var (centroid, maturity, weights) = inflight.TryGetValue(obs.FingerprintId, out var current)
                ? current
                : (obs.Centroid, obs.CentroidMaturity, obs.Weights);

            var working = new AbsorbableObservation
            {
                ObservationId = obs.ObservationId,
                FingerprintId = obs.FingerprintId,
                Vector = obs.Vector,
                Centroid = centroid,
                CentroidMaturity = maturity,
                Weights = weights
            };

            var (newCentroid, newMaturity, newWeights) = await AbsorbAsync(working, ct);
            inflight[obs.FingerprintId] = (newCentroid, newMaturity, newWeights);
        }

        return batch.Count;
    }

    private async Task<(float[] Centroid, int Maturity, float[] Weights)> AbsorbAsync(
        AbsorbableObservation obs, CancellationToken ct)
    {
        // Maturity-weighted mean: every absorbed observation contributes equally to the centroid
        // forever. centroid_new = (centroid * maturity + obs) / (maturity + 1).
        var dim = obs.Centroid.Length;
        var newCentroid = new float[dim];
        var maturity = obs.CentroidMaturity;
        for (var i = 0; i < dim; i++)
            newCentroid[i] = (obs.Centroid[i] * maturity + obs.Vector[i]) / (maturity + 1);

        // Stability learning: dims where the absorbed observation matched the centroid closely
        // get a positive weight nudge for this fingerprint; dims that diverged get a negative.
        var newWeights = (float[])obs.Weights.Clone();
        IdentityWeightMath.ApplyStability(
            newWeights, obs.Vector, obs.Centroid, _options.Weights.StabilityLearningRate);
        IdentityWeightMath.RenormaliseAndClamp(
            newWeights, _options.Weights.MinWeight, _options.Weights.MaxWeight);

        var newMaturity = maturity + 1;
        await _store.AbsorbObservationAsync(
            obs.ObservationId, obs.FingerprintId, newCentroid, newMaturity, newWeights, ct);
        return (newCentroid, newMaturity, newWeights);
    }
}
