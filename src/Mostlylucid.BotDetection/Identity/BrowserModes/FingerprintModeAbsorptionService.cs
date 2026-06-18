using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.BotDetection.Scheduling;
using Mostlylucid.Common.Scheduling;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Drains the append-only <c>fingerprint_mode_observations</c> table on a
///     fixed tick, computing the batched EWMA per (fingerprint_id, mode_id)
///     tuple in a single pass and writing one UPSERT per tuple per tick.
///     Mirrors <see cref="FingerprintAbsorptionService"/> exactly -- same
///     <see cref="IScheduleCoordinator"/>-driven tick shape, same per-tick
///     batch fetch and in-memory grouping. Closes the read-modify-write race
///     the matcher's previous direct-UPSERT absorb had under concurrent
///     requests for the same fingerprint+mode tuple.
///
///     Dormant when <c>BotDetectionOptions.Identity.Enabled</c> or
///     <c>BotDetectionOptions.Identity.BrowserMode.Enabled</c> is false.
///
///     <para>
///         <b>Wave 2 Cat-C* migration.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>while + Task.Delay</c> loop driven by
///         <c>Drift.DriftCheckIntervalSeconds</c>. Now subscribes to
///         <see cref="TickCadence.Tick5m"/> -- the SAME cadence as
///         <see cref="FingerprintAbsorptionService"/> and
///         <see cref="FingerprintRollupRecomputeService"/> -- so all three
///         services land on the same wave window. A rollup recompute on this
///         tick sees consistent parent-absorption + per-mode-absorption state
///         per <c>project_absorption_services_migration</c>.
///     </para>
/// </summary>
public sealed class FingerprintModeAbsorptionService : IDisposable
{
    private readonly ILogger<FingerprintModeAbsorptionService> _logger;
    private readonly IFingerprintBrowserModeStore _modeStore;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;
    private readonly IDisposable? _subscription;
    private int _disposed;

    public FingerprintModeAbsorptionService(
        ILogger<FingerprintModeAbsorptionService> logger,
        IFingerprintBrowserModeStore modeStore,
        IdentityArchetypeRegistry archetypes,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator? scheduleCoordinator = null)
    {
        _logger = logger;
        _modeStore = modeStore;
        _archetypes = archetypes;
        _options = options.Value.Identity;
        _enabled = _options.Enabled && _options.BrowserMode.Enabled;

        if (!_enabled)
        {
            _logger.LogDebug(
                "FingerprintModeAbsorptionService dormant: Identity.Enabled={Identity}, BrowserMode.Enabled={BrowserMode}",
                _options.Enabled, _options.BrowserMode.Enabled);
        }

        // Optional so existing direct-construction tests (MatcherBrowserModeAbsorbTests)
        // keep working without spinning up a real coordinator. The test rigs call
        // TickOnceAsync directly to drive a deterministic drain.
        if (scheduleCoordinator is not null && _enabled)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick5m,
                "FingerprintModeAbsorptionService",
                CostHint.Medium,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Each tick: drain whatever
    ///     unabsorbed mode observations landed since the last tick. Paired with
    ///     <see cref="FingerprintAbsorptionService"/> and
    ///     <see cref="FingerprintRollupRecomputeService"/> on the same Tick5m
    ///     wave window so a rollup recompute sees consistent state.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (!_enabled) return;

        try
        {
            var absorbed = await TickOnceAsync(_options.BrowserMode.DrainMaxRowsPerTick, ct);
            if (absorbed > 0)
                _logger.LogDebug("BrowserMode drain folded {Count} mode observations", absorbed);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutdown; not an error.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BrowserMode drain tick failed");
        }

        // Reconciliation pass: re-classify a bounded batch of pre-existing
        // mode rows against the current matcher contract. Independent of the
        // drain above -- runs even when no new observations arrived this
        // tick, because the rows that need reconciling are the ones with
        // NO recent traffic (the drain reabsorbs the active ones). Bounded
        // by IdentityOptions.BrowserMode.ReconcileMaxRowsPerTick so the pass
        // amortises across ticks instead of spiking CPU after deploy.
        try
        {
            var reclassified = await ReconcileOnceAsync(_options.BrowserMode.ReconcileMaxRowsPerTick, ct);
            if (reclassified > 0)
                _logger.LogInformation("BrowserMode reconcile reclassified {Count} mode rows", reclassified);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BrowserMode reconcile tick failed");
        }
    }

    /// <summary>
    ///     One reconciliation pass. Fetches up to <paramref name="maxRows"/>
    ///     mode rows whose <c>inferred_archetype</c> may be stale relative to
    ///     the current archetype-matcher contract (UA gate + future
    ///     contracts), looks up the parent fingerprint's most-recent
    ///     <c>ua_family</c>, re-runs <see cref="IdentityArchetypeRegistry.FindNearest"/>
    ///     under that gate, and writes the new classification when it
    ///     differs from what the row currently holds. Returns the number of
    ///     rows reclassified (zero when everything was already consistent or
    ///     when the threshold filtered all candidates out).
    ///
    ///     The store filters out rows whose parent never recorded a
    ///     <c>ua_family</c>, so the matcher gate always fires here -- this is
    ///     not a "rerun the unfiltered matcher" pass.
    /// </summary>
    public async Task<int> ReconcileOnceAsync(int maxRows, CancellationToken ct)
    {
        if (maxRows <= 0) return 0;

        var candidates = await _modeStore.ListModeReconciliationCandidatesAsync(maxRows, ct);
        if (candidates.Count == 0) return 0;

        var reclassified = 0;
        foreach (var c in candidates)
        {
            if (ct.IsCancellationRequested) break;
            var match = _archetypes.FindNearest(c.Centroid, c.ParentUaFamily);

            string? newArchetype = null;
            double? newConfidence = null;
            if (match is not null && match.Score >= _options.BrowserMode.MinInferredArchetypeScore)
            {
                newArchetype = match.Archetype.ArchetypeId;
                newConfidence = match.Score;
            }

            // No-op when the row is already consistent with the gated result.
            if (string.Equals(newArchetype, c.CurrentInferredArchetype, StringComparison.OrdinalIgnoreCase))
                continue;

            await _modeStore.UpdateModeInferredArchetypeAsync(
                c.FingerprintId, c.ModeId, newArchetype, newConfidence, ct);
            reclassified++;
            _logger.LogDebug(
                "Reconciled mode row {Fp}/{Mode}: {Old} -> {New} (ua={Ua})",
                c.FingerprintId, c.ModeId,
                c.CurrentInferredArchetype ?? "(none)",
                newArchetype ?? "(none)",
                c.ParentUaFamily);
        }
        return reclassified;
    }

    /// <summary>
    ///     One drain pass: fetch up to <paramref name="maxRows"/> unabsorbed
    ///     observations, group by (fingerprint_id, mode_id), compute the
    ///     batched EWMA per tuple, write one UPSERT + one absorbed-at update
    ///     transaction per tuple. Returns the number of observations folded.
    /// </summary>
    public async Task<int> TickOnceAsync(int maxRows, CancellationToken ct)
    {
        var batch = await _modeStore.ListUnabsorbedModeObservationsAsync(maxRows, ct);
        if (batch.Count == 0) return 0;

        // Group in document order -- the store returns rows sorted by
        // (fingerprint_id, mode_id, id), so adjacent rows share a tuple.
        var folded = 0;
        var i = 0;
        while (i < batch.Count)
        {
            var first = batch[i];
            var j = i + 1;
            while (j < batch.Count
                   && string.Equals(batch[j].FingerprintId, first.FingerprintId, StringComparison.Ordinal)
                   && string.Equals(batch[j].ModeId, first.ModeId, StringComparison.OrdinalIgnoreCase))
                j++;

            await AbsorbTupleAsync(first.FingerprintId, first.ModeId, batch, start: i, count: j - i, ct);
            folded += j - i;
            i = j;
        }

        return folded;
    }

    /// <summary>
    ///     Fold <paramref name="count"/> observations starting at
    ///     <paramref name="start"/> into the (fingerprint_id, mode_id) tuple's
    ///     mode row. EWMA over N samples:
    ///       new_centroid = (old * old_maturity + sum(obs)) / (old_maturity + N)
    ///     -- mathematically identical to applying the single-step EWMA N
    ///     times in order, but one tuple write covers the whole batch.
    /// </summary>
    private async Task AbsorbTupleAsync(
        string fingerprintId,
        string modeId,
        IReadOnlyList<UnabsorbedModeObservation> batch,
        int start,
        int count,
        CancellationToken ct)
    {
        var existing = await _modeStore.GetModeAsync(fingerprintId, modeId, ct);
        var dim = batch[start].Vector.Length;
        var firstSeen = existing?.FirstSeen ?? batch[start].ObservedAt;
        var lastSeen = batch[start + count - 1].ObservedAt;

        float[] mergedCentroid;
        int newMaturity;
        int newObservationCount;
        float[] weights;

        if (existing is null)
        {
            // Seed from the batch: average the N vectors (maturity-weighted
            // starts at 1 for the first observation, so the batched form is
            // straight mean).
            mergedCentroid = new float[dim];
            for (var k = 0; k < count; k++)
            {
                var v = batch[start + k].Vector;
                for (var d = 0; d < dim && d < v.Length; d++) mergedCentroid[d] += v[d];
            }
            for (var d = 0; d < dim; d++) mergedCentroid[d] /= count;
            newMaturity = count;
            newObservationCount = count;
            weights = new float[dim];
            for (var d = 0; d < dim; d++) weights[d] = 1.0f;
        }
        else
        {
            mergedCentroid = new float[dim];
            var oldMat = existing.CentroidMaturity;
            for (var d = 0; d < dim && d < existing.Centroid.Length; d++)
                mergedCentroid[d] = existing.Centroid[d] * oldMat;
            for (var k = 0; k < count; k++)
            {
                var v = batch[start + k].Vector;
                for (var d = 0; d < dim && d < v.Length; d++) mergedCentroid[d] += v[d];
            }
            var newMat = oldMat + count;
            for (var d = 0; d < dim; d++) mergedCentroid[d] /= newMat;
            newMaturity = newMat;
            newObservationCount = existing.ObservationCount + count;
            weights = existing.Weights;
        }

        // Recompute the per-mode nearest archetype against the freshly merged centroid.
        // Mirrors what FingerprintAbsorptionService does for the parent fingerprint:
        // every absorption recomputes the inferred archetype, so the per-mode row's
        // "Nearest archetype" cell on the signature detail tracks the centroid as it
        // evolves. Gated by IdentityOptions.BrowserMode.MinInferredArchetypeScore so
        // sparse / noisy modes don't latch onto an umbrella centroid -- under threshold
        // the field stays null and the UI renders "-" (explicit "no confident match"
        // beats a confident-looking false positive).
        //
        // Per project_centroid_learning_feedback_loop, the same registry will eventually
        // hold BDF-derived archetypes alongside the hand-curated YAML; this call is the
        // single read-site they both feed.
        // Pick the most recently observed UA family in the batch as the gate input. The
        // batch covers one (fingerprint, mode) tuple's recent observations so the family
        // is typically stable across them; "latest in the batch" matches what the operator
        // sees as the current request's claim. Falls back to null when the column was
        // null on every row (legacy data); the matcher treats null as "no gate".
        string? batchUaFamily = null;
        for (var k = count - 1; k >= 0; k--)
        {
            var ua = batch[start + k].UaFamily;
            if (!string.IsNullOrEmpty(ua)) { batchUaFamily = ua; break; }
        }

        string? inferredArchetype = null;
        double? inferredConfidence = null;
        var nearest = _archetypes.FindNearest(mergedCentroid, batchUaFamily);
        if (nearest is not null && nearest.Score >= _options.BrowserMode.MinInferredArchetypeScore)
        {
            inferredArchetype = nearest.Archetype.ArchetypeId;
            inferredConfidence = nearest.Score;
        }

        var updated = new FingerprintBrowserMode
        {
            FingerprintId = fingerprintId,
            ModeId = modeId,
            Centroid = mergedCentroid,
            CentroidMaturity = newMaturity,
            Weights = weights,
            ObservationCount = newObservationCount,
            FirstSeen = firstSeen,
            LastSeen = lastSeen,
            InferredArchetype = inferredArchetype,
            InferredConfidence = inferredConfidence,
        };

        var ids = new long[count];
        for (var k = 0; k < count; k++) ids[k] = batch[start + k].ObservationId;
        await _modeStore.AbsorbModeObservationsAsync(updated, ids, ct);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }
}
