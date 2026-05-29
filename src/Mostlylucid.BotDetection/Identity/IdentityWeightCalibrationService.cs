using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Models;

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
/// </summary>
public sealed class IdentityWeightCalibrationService : BackgroundService
{
    private readonly ILogger<IdentityWeightCalibrationService> _logger;
    private readonly IFingerprintStore _store;
    private readonly IdentityArchetypeRegistry _archetypes;
    private readonly IdentityOptions _options;
    private readonly bool _enabled;

    public IdentityWeightCalibrationService(
        ILogger<IdentityWeightCalibrationService> logger,
        IFingerprintStore store,
        IdentityArchetypeRegistry archetypes,
        IOptions<BotDetectionOptions> options)
    {
        _logger = logger;
        _store = store;
        _archetypes = archetypes;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled)
        {
            _logger.LogDebug("IdentityWeightCalibrationService dormant: Identity.Enabled = false");
            return;
        }

        var tick = TimeSpan.FromMinutes(Math.Max(1, _options.Calibration.CalibrationIntervalMinutes));

        while (!stoppingToken.IsCancellationRequested)
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Calibration tick failed");
            }

            try { await Task.Delay(tick, stoppingToken); }
            catch (OperationCanceledException) { return; }
        }
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
