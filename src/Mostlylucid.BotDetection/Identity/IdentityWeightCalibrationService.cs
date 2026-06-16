using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;
using Mostlylucid.Common.Scheduling;
using Mostlylucid.BotDetection.Scheduling;

namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Background calibration tick. Two responsibilities, both running on the same cadence:
///
///     1. Compute global per-dim weights via the Fisher discriminant ratio over the population
///        of fingerprints, grouped by their inferred client type. High-discriminating dims
///        (those that vary more across client types than within) get amplified; low-signal dims
///        get suppressed. Persisted to <c>identity_dimension_weights</c> for the matcher to pick
///        up on its refresh cadence.
///
///     2. Self-refine each archetype centroid by blending in the mean of its descendant
///        fingerprints (descendants = fps whose nearest archetype is this one). Capped α
///        prevents an archetype from drifting away from its seeded shape in any single cycle,
///        and the YAML-defined <c>dimension_mask</c> stays untouched — only the centroid
///        learns. Persisted via <see cref="IFingerprintStore.UpsertArchetypeAsync"/> and
///        pushed back into the in-memory registry via <see cref="IdentityArchetypeRegistry.Replace"/>.
///
///     Dormant when <c>BotDetectionOptions.Identity.Enabled</c> is false.
///     <para>
///         <b>Wave 2 architectural-drift remediation.</b> Was a
///         <see cref="Microsoft.Extensions.Hosting.BackgroundService"/> with a
///         <c>Task.Delay(CalibrationIntervalMinutes)</c> loop (default 30 min,
///         min 1 min); now subscribes to <see cref="TickCadence.Tick1m"/> and
///         gates each <see cref="RunOnceAsync"/> pass on "last-success older
///         than the configured interval". Inner calibration math unchanged.
///     </para>
/// </summary>
public sealed class IdentityWeightCalibrationService : IDisposable
{
    private readonly ILogger<IdentityWeightCalibrationService> _logger;
    private readonly IFingerprintStore _store;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;
    private readonly IDisposable? _subscription;
    private DateTime _lastSuccessfulRunUtc = DateTime.MinValue;
    private int _disposed;

    public IdentityWeightCalibrationService(
        ILogger<IdentityWeightCalibrationService> logger,
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

        // Optional so test fixtures that drive RunOnceAsync directly (without
        // scheduling) keep working. Production DI passes the real coordinator.
        if (scheduleCoordinator is not null)
        {
            _subscription = scheduleCoordinator.Subscribe(
                TickCadence.Tick1m,
                "IdentityWeightCalibrationService",
                CostHint.High,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     ScheduleCoordinator tick handler. Fires every Tick1m; gates the
    ///     calibration pass on "last-success older than configured
    ///     CalibrationIntervalMinutes" so a 30-minute configured interval
    ///     fires roughly every 30 ticks while a 1-minute interval fires every
    ///     tick. Dormant when Identity.Enabled is false. Public so tests can
    ///     drive a single beat deterministically.
    /// </summary>
    public async Task OnTickAsync(DateTimeOffset now, CancellationToken ct)
    {
        if (_disposed != 0) return;
        if (!_enabled) return;

        var interval = TimeSpan.FromMinutes(Math.Max(1, _options.Calibration.CalibrationIntervalMinutes));
        if (_lastSuccessfulRunUtc != DateTime.MinValue &&
            now.UtcDateTime - _lastSuccessfulRunUtc < interval)
        {
            return; // Not yet due.
        }

        try
        {
            await RunOnceAsync(ct);
            _lastSuccessfulRunUtc = DateTime.UtcNow;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calibration tick failed");
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _subscription?.Dispose(); }
        catch { /* coordinator already torn down */ }
    }

    /// <summary>
    ///     One full calibration pass: list fingerprints, compute Fisher weights across
    ///     inferred-client-type clusters, persist; then refine each archetype centroid against
    ///     its descendants and push the refreshed registry. Returns counts for diagnostics.
    /// </summary>
    public async Task<CalibrationResult> RunOnceAsync(CancellationToken ct)
    {
        var fingerprints = await _store.ListFingerprintsAsync(ct);
        var dimension = _store.Layout.Dimension;

        // Cluster membership comes from the fingerprint's inferred client type, which itself
        // comes from the nearest-archetype scan in absorption. No external clustering needed.
        var clusterMembers = fingerprints
            .Select(fp => (ClusterId: fp.InferredClientType, fp.Centroid))
            .ToList();

        var weights = IdentityWeightMath.ComputeFisherWeights(
            clusterMembers,
            dimension,
            minFingerprints: 2,
            minWeight: _options.Weights.MinWeight,
            maxWeight: _options.Weights.MaxWeight);

        var clustersUsed = clusterMembers
            .Select(m => m.ClusterId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        if (weights is not null)
        {
            await _store.UpsertGlobalWeightsAsync(weights, fingerprints.Count, clustersUsed,
                _archetypes.All.Count, ct);
            _logger.LogInformation(
                "Calibration computed global weights from {Fps} fingerprints across {Clusters} clusters",
                fingerprints.Count, clustersUsed);
        }
        else
        {
            _logger.LogDebug(
                "Calibration skipped weights: {Fps} fingerprints across {Clusters} clusters insufficient",
                fingerprints.Count, clustersUsed);
        }

        // Archetype self-refinement.
        var refined = new List<IdentityArchetype>();
        var refinedCount = 0;
        foreach (var archetype in _archetypes.All)
        {
            var descendants = fingerprints
                .Where(fp => string.Equals(fp.InferredClientType, archetype.ArchetypeId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(fp => fp.Centroid)
                .ToList();

            var newCentroid = IdentityWeightMath.RefineArchetypeCentroid(
                archetype.Centroid, descendants, _options.Calibration.ArchetypeRefinementCap);

            if (newCentroid is null)
            {
                refined.Add(archetype);
                continue;
            }

            var updated = archetype with
            {
                Centroid = newCentroid,
                DescendantCount = descendants.Count,
                LastRefinedAt = DateTime.UtcNow
            };
            refined.Add(updated);
            await _store.UpsertArchetypeAsync(updated, ct);
            refinedCount++;
        }

        if (refinedCount > 0)
        {
            _archetypes.Replace(refined);
            _logger.LogInformation(
                "Refined {Count} archetypes from descendant fingerprint centroids", refinedCount);
        }

        return new CalibrationResult(
            FingerprintCount: fingerprints.Count,
            ClustersUsed: clustersUsed,
            WeightsComputed: weights is not null,
            ArchetypesRefined: refinedCount);
    }
}

public sealed record CalibrationResult(
    int FingerprintCount,
    int ClustersUsed,
    bool WeightsComputed,
    int ArchetypesRefined);
