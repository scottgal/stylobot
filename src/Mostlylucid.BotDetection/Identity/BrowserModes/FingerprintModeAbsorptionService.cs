using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Drains the append-only <c>fingerprint_mode_observations</c> table on a
///     fixed tick, computing the batched EWMA per (fingerprint_id, mode_id)
///     tuple in a single pass and writing one UPSERT per tuple per tick.
///     Mirrors <see cref="FingerprintAbsorptionService"/> exactly — same
///     BackgroundService shape, same fixed-cadence loop, same per-tick batch
///     fetch and in-memory grouping. Closes the read-modify-write race the
///     matcher's previous direct-UPSERT absorb had under concurrent requests
///     for the same fingerprint+mode tuple.
///
///     Dormant when <c>BotDetectionOptions.Identity.Enabled</c> or
///     <c>BotDetectionOptions.Identity.BrowserMode.Enabled</c> is false.
///
///     NOTE: this service uses <see cref="BackgroundService"/> to match the
///     parent <see cref="FingerprintAbsorptionService"/>'s existing shape,
///     which the project rule [[feedback_no_background_services]] flags as
///     drift to migrate when the schedule coordinator lands. Both services
///     should migrate to the coordinator + tick-signal subscription pattern
///     in the same pass to keep the absorption semantics aligned.
/// </summary>
public sealed class FingerprintModeAbsorptionService : BackgroundService
{
    private readonly ILogger<FingerprintModeAbsorptionService> _logger;
    private readonly IFingerprintBrowserModeStore _modeStore;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;

    public FingerprintModeAbsorptionService(
        ILogger<FingerprintModeAbsorptionService> logger,
        IFingerprintBrowserModeStore modeStore,
        IdentityArchetypeRegistry archetypes,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _modeStore = modeStore;
        _archetypes = archetypes;
        _options = options.Value.Identity;
        _enabled = _options.Enabled && _options.BrowserMode.Enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug(
                "FingerprintModeAbsorptionService dormant: Identity.Enabled={Identity}, BrowserMode.Enabled={BrowserMode}",
                _options.Enabled, _options.BrowserMode.Enabled);
            return;
        }

        // Same cadence as FingerprintAbsorptionService so per-mode and parent
        // absorptions land in the same wave window. The DriftCheckInterval is
        // the closest existing knob; a future BrowserModeOptions.DrainInterval
        // can split the cadences if profiling justifies it.
        var tick = TimeSpan.FromSeconds(Math.Max(1, _options.Drift.DriftCheckIntervalSeconds));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var absorbed = await TickOnceAsync(_options.BrowserMode.DrainMaxRowsPerTick, stoppingToken);
                if (absorbed > 0)
                    _logger.LogDebug("BrowserMode drain folded {Count} mode observations", absorbed);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "BrowserMode drain tick failed");
            }

            try { await Task.Delay(tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
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

        // Group in document order — the store returns rows sorted by
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
        // sparse / noisy modes don't latch onto an umbrella centroid — under threshold
        // the field stays null and the UI renders "-" (explicit "no confident match"
        // beats a confident-looking false positive).
        //
        // Per project_centroid_learning_feedback_loop, the same registry will eventually
        // hold BDF-derived archetypes alongside the hand-curated YAML; this call is the
        // single read-site they both feed.
        string? inferredArchetype = null;
        double? inferredConfidence = null;
        var nearest = _archetypes.FindNearest(mergedCentroid);
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
}
