using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Background absorption: folds detailed observations into fingerprint centroids using
///     maturity-weighted means, applies the per-fingerprint stability learning signal, and
///     bumps the fingerprint's centroid_maturity. Per the spec's feedback latency tier,
///     "per-fingerprint absorption fires per maturity threshold" -- this service
///     subscribes to <see cref="IFingerprintStore.ObservationAppended"/> for the hot path
///     (per-fp debounce, absorbs within ~250ms of the first new observation) and runs a
///     safety-net backstop sweep on the schedule-coordinator's <see cref="TickCadence.Tick5m"/>
///     cadence for crash recovery and missed events.
///
///     Inferred client type recompute and archetype refinement live in later slices; this
///     service is purely the centroid + stability path.
///
///     Dormant when <c>BotDetectionOptions.Identity.Enabled</c> is false.
///     <para>
///         <b>Wave 2 Cat-C* migration.</b> Was a <see cref="Microsoft.Extensions.Hosting.BackgroundService"/>
///         whose <c>ExecuteAsync</c> ran a <c>while + Task.Delay(backstop)</c> loop. Now subscribes to
///         <see cref="TickCadence.Tick5m"/> via <see cref="IScheduleCoordinator"/>; each tick runs
///         the backstop sweep. The inline event-driven debounced fast-path
///         (<see cref="OnObservationAppended"/>) is preserved -- the tick is the catch-up for
///         observations the debounce path didn't pick up. Subscribed at the SAME cadence as
///         <see cref="BrowserModes.FingerprintModeAbsorptionService"/> and
///         <see cref="BrowserModes.FingerprintRollupRecomputeService"/> so all three sit on
///         the same wave window per <c>project_absorption_services_migration</c>: a rollup
///         recompute on Tick5m sees consistent parent-absorption + per-mode-absorption state.
///     </para>
/// </summary>
public sealed class FingerprintAbsorptionService : IDisposable
{
    private readonly ILogger<FingerprintAbsorptionService> _logger;
    private readonly IFingerprintStore _store;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;

    // Per-fp debounce: maps fingerprint id to the next-fire deadline.
    private readonly ConcurrentDictionary<string, DateTime> _pendingByFingerprint
        = new(StringComparer.Ordinal);

    // Captured handler so Dispose can unhook the CLR event subscription (B10).
    private readonly Action<string> _onObservationAppendedHandler;
    private readonly IDisposable? _subscription;

    private readonly CancellationTokenSource _serviceCts = new();
    private int _disposed;

    // Test instrumentation: counts how many event-driven absorptions and backstop ticks have run.
    // internal so tests in Mostlylucid.BotDetection.Test can read them without leaking to callers.
    internal int EventDrivenAbsorptionCount;
    internal int BackstopSweepCount;

    public FingerprintAbsorptionService(
        ILogger<FingerprintAbsorptionService> logger,
        IFingerprintStore store,
        IdentityArchetypeRegistry archetypes,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _logger = logger;
        _store = store;
        _archetypes = archetypes;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;

        // Wire the event-driven fast-path immediately so observations recorded
        // between construction and the first tick still get folded inside the
        // debounce window. Capture the handler delegate so Dispose can unhook
        // it -- otherwise the store keeps a stale subscription after disposal
        // and post-dispose ObservationAppended events would enqueue work for
        // a torn-down service (B10).
        _onObservationAppendedHandler = OnObservationAppended;
        if (_enabled)
        {
            _store.ObservationAppended += _onObservationAppendedHandler;
        }
        else
        {
            _logger.LogDebug("FingerprintAbsorptionService dormant: Identity.Enabled = false");
        }

        // Optional so existing direct-construction tests (MatcherBrowserModeAbsorbTests,
        // FingerprintAbsorptionServiceSubscribeTests' debounce + backstop coverage) keep
        // working without spinning up a real coordinator.
        if (scheduleCoordinator is not null && _enabled)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick5m,
                "FingerprintAbsorptionService",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: run the backstop sweep
    ///     across all fingerprints with pending observations. Catches anything
    ///     the per-fp debounced fast-path missed (handler exception, crash
    ///     recovery, late SQLite rows). Paired with
    ///     <see cref="BrowserModes.FingerprintModeAbsorptionService"/> and
    ///     <see cref="BrowserModes.FingerprintRollupRecomputeService"/> on the
    ///     same Tick5m wave window so a rollup recompute sees consistent
    ///     parent + per-mode state.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (!_enabled) return;

        try
        {
            var absorbed = await TickOnceAsync(ct);
            Interlocked.Increment(ref BackstopSweepCount);
            if (absorbed > 0)
                _logger.LogDebug("Backstop absorption tick folded {Count} observations", absorbed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown; not an error.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Backstop absorption tick failed");
        }
    }

    private void OnObservationAppended(string fingerprintId)
    {
        if (_disposed != 0) return;

        // Stamp the next-fire deadline; race-tolerant.
        var debounceMs = Math.Max(1, _options.Vector.SubscriptionDebounceMs);
        var fire = DateTime.UtcNow.AddMilliseconds(debounceMs);

        if (!_pendingByFingerprint.TryAdd(fingerprintId, fire))
        {
            // Already scheduled; push the deadline out (debounces bursts).
            _pendingByFingerprint[fingerprintId] = fire;
            return;
        }

        // Fire-and-forget task: waits the debounce window then absorbs this fingerprint.
        // Bound the lifetime by the service's CTS so dispose halts in-flight debounces.
        var ct = _serviceCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(debounceMs, ct).ConfigureAwait(false);
                _pendingByFingerprint.TryRemove(fingerprintId, out _);
                if (_disposed != 0) return;
                await RunAbsorptionForAsync(fingerprintId, ct).ConfigureAwait(false);
                Interlocked.Increment(ref EventDrivenAbsorptionCount);
            }
            catch (OperationCanceledException)
            {
                // Shutdown; nothing to log.
                _pendingByFingerprint.TryRemove(fingerprintId, out _);
            }
            catch (Exception ex)
            {
                _pendingByFingerprint.TryRemove(fingerprintId, out _);
                _logger.LogWarning(ex, "Event-driven absorption failed for {Id}", fingerprintId);
            }
        }, ct);
    }

    /// <summary>
    ///     Absorbs all pending observations for a single fingerprint. Called both by the
    ///     event-driven handler (per-fp debounce) and, indirectly, by the backstop loop
    ///     via <see cref="TickOnceAsync"/> which walks the full pending set.
    /// </summary>
    private async Task RunAbsorptionForAsync(string fingerprintId, CancellationToken ct)
    {
        var batch = await _store.ListAbsorbableObservationsAsync(
            _options.Vector.AbsorptionMaturityThreshold,
            _options.Vector.AbsorptionAgeDays,
            _options.Vector.ActiveWindowDays,
            ct);

        // Filter to just this fingerprint.
        var forFp = batch
            .Where(o => string.Equals(o.FingerprintId, fingerprintId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (forFp.Count == 0) return;

        // Track in-flight state so multiple observations fold sequentially.
        (float[] Centroid, int Maturity, float[] Weights, string InferredType)? inflight = null;

        foreach (var obs in forFp)
        {
            var current = inflight ?? (obs.Centroid, obs.CentroidMaturity, obs.Weights, obs.InferredClientType);

            var working = new AbsorbableObservation
            {
                ObservationId = obs.ObservationId,
                FingerprintId = obs.FingerprintId,
                Vector = obs.Vector,
                Centroid = current.Item1,
                CentroidMaturity = current.Item2,
                Weights = current.Item3,
                InferredClientType = current.Item4
            };

            var result = await AbsorbAsync(working, ct);
            inflight = result;
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

        // Track in-flight per-fingerprint state so multiple absorptions in the same tick fold
        // sequentially against the latest values, not against the stale row.
        var inflight = new Dictionary<string, (float[] Centroid, int Maturity, float[] Weights, string InferredType)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var obs in batch)
        {
            var current = inflight.TryGetValue(obs.FingerprintId, out var cached)
                ? cached
                : (obs.Centroid, obs.CentroidMaturity, obs.Weights, obs.InferredClientType);

            var working = new AbsorbableObservation
            {
                ObservationId = obs.ObservationId,
                FingerprintId = obs.FingerprintId,
                Vector = obs.Vector,
                Centroid = current.Item1,
                CentroidMaturity = current.Item2,
                Weights = current.Item3,
                InferredClientType = current.Item4
            };

            var (newCentroid, newMaturity, newWeights, newInferredType) = await AbsorbAsync(working, ct);
            inflight[obs.FingerprintId] = (newCentroid, newMaturity, newWeights, newInferredType);
        }

        return batch.Count;
    }

    private async Task<(float[] Centroid, int Maturity, float[] Weights, string InferredType)> AbsorbAsync(
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

        // Recompute inferred client type against the new centroid. If the nearest archetype has
        // changed, the fingerprint's behavioural classification has drifted; the next request
        // will emit identity.client_type_drift.
        var nearest = _archetypes.FindNearest(newCentroid);
        var newInferredType = nearest?.Archetype.ArchetypeId ?? obs.InferredClientType;
        var newInferredConfidence = nearest?.Score ?? 0.0;
        var typeChanged = !string.Equals(newInferredType, obs.InferredClientType, StringComparison.OrdinalIgnoreCase);

        var newMaturity = maturity + 1;
        await _store.AbsorbObservationAsync(
            obs.ObservationId, obs.FingerprintId, newCentroid, newMaturity, newWeights,
            newInferredClientType: newInferredType,
            newInferredTypeConfidence: newInferredConfidence,
            inferredTypeChanged: typeChanged,
            ct);

        if (typeChanged)
        {
            _logger.LogInformation(
                "Fingerprint {Id} drifted: {Old} -> {New} (confidence {Conf:F2})",
                obs.FingerprintId, obs.InferredClientType, newInferredType, newInferredConfidence);

            // The inferred archetype flipped (e.g. bot -> human), so the persisted display
            // name is now stale -- a "Googlebot" name must not survive on a fingerprint that
            // drifted human. Clear it; the matcher re-derives the name from the new archetype
            // on the next request via EmitDisplayNameSignal's recompose-on-empty path. Rule:
            // if the centroid flips the classification, the name must change.
            await _store.UpdateDisplayNameAsync(obs.FingerprintId, string.Empty, DateTime.UtcNow, ct);
        }

        return (newCentroid, newMaturity, newWeights, newInferredType);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        // B10: unhook the CLR event subscription on the upstream store BEFORE
        // releasing the tick subscription. Otherwise post-dispose
        // ObservationAppended events would still enqueue debounce tasks for a
        // service that's been torn down.
        try { _store.ObservationAppended -= _onObservationAppendedHandler; }
        catch { /* event source may already be torn down */ }
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
        try { _serviceCts.Cancel(); }
        catch { /* idempotent */ }
        try { _serviceCts.Dispose(); }
        catch { /* idempotent */ }
    }
}
