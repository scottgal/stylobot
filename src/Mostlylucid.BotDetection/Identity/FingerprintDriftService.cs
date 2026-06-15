using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;

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
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(DriftCheckIntervalSeconds)</c> loop (default 5 s);
///         now subscribes to <see cref="TickCadence.Tick10s"/> and gates each
///         pass on "last-success older than configured
///         <see cref="IdentityDriftOptions.DriftCheckIntervalSeconds"/>" -- so
///         a 5-second configured interval still fires every other tick while
///         longer intervals throttle accordingly. The inner work is already
///         gated by <c>cached_score_updated_at &lt; TTL</c> in the store, so a
///         slightly stretched cadence costs nothing under steady state.
///     </para>
/// </summary>
public sealed class FingerprintDriftService : IDisposable
{
    private readonly ILogger<FingerprintDriftService> _logger;
    private readonly IFingerprintStore _store;
    private readonly IdentityGlobalWeightsCache _globalWeights;
    private readonly IdentityProcessingCoordinator _coordinator;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;
    private readonly IDisposable _subscription;
    private DateTime _lastSuccessfulTickUtc = DateTime.MinValue;
    private int _disposed;

    public FingerprintDriftService(
        ILogger<FingerprintDriftService> logger,
        IFingerprintStore store,
        IdentityGlobalWeightsCache globalWeights,
        IdentityProcessingCoordinator coordinator,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator scheduleCoordinator)
    {
        ArgumentNullException.ThrowIfNull(scheduleCoordinator);
        _logger = logger;
        _store = store;
        _globalWeights = globalWeights;
        _coordinator = coordinator;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;

        _subscription = scheduleCoordinator.Subscribe(
            TickCadence.Tick10s,
            "FingerprintDriftService",
            CostHint.Medium,
            OnTickAsync);
    }

    /// <summary>
    ///     The schedule-coordinator tick handler. Fires every Tick10s; gates
    ///     each call to <see cref="TickOnceAsync"/> on "last-success older
    ///     than configured DriftCheckIntervalSeconds" so the inner pass
    ///     cadence honours the option. Dormant when
    ///     <c>Identity.Enabled</c> is false. Public so tests can drive a
    ///     single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (!_enabled) return;

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.Drift.DriftCheckIntervalSeconds));
        if (_lastSuccessfulTickUtc != DateTime.MinValue &&
            now.UtcDateTime - _lastSuccessfulTickUtc < interval)
        {
            return; // Not yet due.
        }

        try
        {
            var (checks, drifts) = await TickOnceAsync(ct);
            _lastSuccessfulTickUtc = DateTime.UtcNow;
            if (checks > 0)
                _logger.LogDebug("Drift tick: {Checked} verified, {Drifts} drift", checks, drifts);
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Drift tick failed");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription.Dispose(); }
        catch { /* coordinator already torn down */ }
    }

    /// <summary>
    ///     On-demand single-fingerprint verification. Used by the dashboard "Re-verify"
    ///     button so an operator can force a fresh L2 check without waiting for the
    ///     <c>CachedScoreTtlSeconds</c> gate. Computes the same weighted cosine the
    ///     scheduled tick uses, returns the score + whether it crossed the drift
    ///     threshold, and bumps <c>cached_score_updated_at</c> so the row reflects the
    ///     fresh verification.
    /// </summary>
    public async Task<DriftVerificationResult?> VerifyOneAsync(string fingerprintId, CancellationToken ct)
    {
        // Operator-triggered: routed through the coordinator with OperatorReverify priority
        // bias so a "Re-verify" click always preempts background work and never sheds when
        // the breaker is open. Risk score doesn't matter for operator priority but we pass
        // a high value (1.0) for symmetry with the matcher.
        var (result, _) = await _coordinator.RunAsync<DriftVerificationResult?>(
            fingerprintId: fingerprintId,
            kind: IdentitySlowPathKind.OperatorReverify,
            riskScore: 1.0,
            operation: async runCt =>
            {
                var fp = await _store.GetFingerprintAsync(fingerprintId, runCt);
                if (fp is null) return null;

                var latest = await _store.GetLatestObservationVectorAsync(fingerprintId, runCt);
                if (latest is null)
                    return new DriftVerificationResult(
                        FingerprintId: fingerprintId,
                        Score: 0,
                        Drifted: false,
                        ObservationFound: false);

                var composed = _globalWeights.Compose(fp.Weights);
                var score = BruteForceIdentityAnchorIndex.WeightedCosine(latest, fp.Centroid, composed);
                var drifted = score < _options.Drift.DriftWarningThreshold;
                if (drifted)
                    _logger.LogInformation(
                        "On-demand drift check: fingerprint {Id} score={Score:F3} below {Threshold:F3}",
                        fingerprintId, score, _options.Drift.DriftWarningThreshold);

                await _store.BumpCachedScoreCheckedAtAsync(fingerprintId, runCt);
                return new DriftVerificationResult(
                    FingerprintId: fingerprintId,
                    Score: score,
                    Drifted: drifted,
                    ObservationFound: true);
            },
            ct: ct);
        return result;
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

/// <summary>
///     Result of an on-demand drift verification triggered from the dashboard. Distinct
///     from the scheduled tick's aggregate counters because the operator wants per-fingerprint
///     visibility into what just happened.
/// </summary>
public sealed record DriftVerificationResult(
    string FingerprintId,
    double Score,
    bool Drifted,
    bool ObservationFound);
