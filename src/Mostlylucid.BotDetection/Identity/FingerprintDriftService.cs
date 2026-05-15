using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Background L2 verifier. Re-checks fingerprints whose <c>cached_score_updated_at</c> is
///     stale by running the most recent observation vector through weighted-cosine against the
///     fingerprint's own centroid + weights. If the score has dropped below
///     <see cref="IdentityDriftOptions.DriftWarningThreshold"/>, the fingerprint's behaviour
///     has drifted away from its identity shape — emit a warning so an L1-confirmed
///     "passes-as-human" verdict cannot persist indefinitely without L2 agreement.
///
///     This is the closing leg of the closed-loop learning system per the spec's "L1 still
///     observes" guarantee: even when fast path skips L2, the drift service eventually catches
///     drift between the cached verdict and the current shape.
///
///     Future slices wire drift detections into <c>cached_bot_probability</c> updates and a
///     dashboard drift-rate column. This slice persists nothing about the drift event itself
///     beyond a structured log line; <c>cached_score_updated_at</c> is bumped on every check.
///
///     Dormant when <c>BotDetectionOptions.Identity.Enabled</c> is false.
/// </summary>
public sealed class FingerprintDriftService : BackgroundService
{
    private readonly ILogger<FingerprintDriftService> _logger;
    private readonly SqliteFingerprintStore _store;
    private readonly IdentityGlobalWeightsCache _globalWeights;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;

    public FingerprintDriftService(
        ILogger<FingerprintDriftService> logger,
        SqliteFingerprintStore store,
        IdentityGlobalWeightsCache globalWeights,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _globalWeights = globalWeights;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("FingerprintDriftService dormant: Identity.Enabled = false");
            return;
        }

        var tick = TimeSpan.FromSeconds(Math.Max(1, _options.Drift.DriftCheckIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var (checks, drifts) = await TickOnceAsync(stoppingToken);
                if (checks > 0)
                    _logger.LogDebug("Drift tick: {Checked} verified, {Drifts} drift", checks, drifts);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Drift tick failed");
            }

            try { await Task.Delay(tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>One pass over stale fingerprints. Returns (checked, drift-detected).</summary>
    public async Task<(int Checked, int Drifts)> TickOnceAsync(CancellationToken ct)
    {
        var stale = await _store.ListStaleScoreFingerprintsAsync(
            _options.Drift.CachedScoreTtlSeconds,
            _options.Drift.DriftBatchSize,
            ct);
        if (stale.Count == 0) return (0, 0);

        var drifts = 0;
        foreach (var fp in stale)
        {
            var latest = await _store.GetLatestObservationVectorAsync(fp.FingerprintId, ct);
            if (latest is null)
            {
                // Race: observation list said >0 but no rows came back. Skip; we'll retry next tick.
                continue;
            }

            var composed = _globalWeights.Compose(fp.Weights);
            var score = BruteForceIdentityAnchorIndex.WeightedCosine(latest, fp.Centroid, composed);
            if (score < _options.Drift.DriftWarningThreshold)
            {
                drifts++;
                _logger.LogWarning(
                    "Drift detected: fingerprint {Id} ({ClientType}) score={Score:F3} below {Threshold:F3}; " +
                    "cached_bot_prob={CachedProb:F2} maturity={Maturity}",
                    fp.FingerprintId, fp.InferredClientType, score, _options.Drift.DriftWarningThreshold,
                    fp.CachedBotProbability, fp.CentroidMaturity);
            }

            await _store.BumpCachedScoreCheckedAtAsync(fp.FingerprintId, ct);
        }

        return (stale.Count, drifts);
    }
}
