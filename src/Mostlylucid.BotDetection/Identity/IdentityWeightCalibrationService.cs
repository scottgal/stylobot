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

    /// <summary>
    ///     Adaptive trigger signal source. Null when the service is constructed
    ///     directly in tests (gate falls back to the legacy CalibrationIntervalMinutes
    ///     check). DI registers <see cref="Triggers.CalibrationSignalSource"/>.
    /// </summary>
    private readonly Triggers.IAdaptiveTriggerSignalSource? _triggerSignals;

    /// <summary>Optional load-band source for the trigger ceiling. Null = treat as Low.</summary>
    private readonly Services.ILoadBandSource? _loadBandSource;

    private DateTime _lastSuccessfulRunUtc = DateTime.MinValue;
    private int _initialColdSeedDone;
    private int _disposed;

    /// <summary>
    ///     Most recent trigger decision + reason, for the
    ///     <c>/admin/learning/health</c> observability endpoint. Volatile so a
    ///     dashboard read on a different thread sees the latest published value
    ///     without a lock.
    /// </summary>
    private volatile TriggerDecisionSnapshot _lastDecision = TriggerDecisionSnapshot.Empty;

    public IdentityWeightCalibrationService(
        ILogger<IdentityWeightCalibrationService> logger,
        IFingerprintStore store,
        IdentityArchetypeRegistry archetypes,
        IOptions<BotDetectionOptions> options,
        IScheduleCoordinator? scheduleCoordinator = null,
        Triggers.IAdaptiveTriggerSignalSource? triggerSignals = null,
        Services.ILoadBandSource? loadBandSource = null)
    {
        _logger = logger;
        _store = store;
        _archetypes = archetypes;
        _options = options.Value.Identity;
        _enabled = _options.Enabled;
        _triggerSignals = triggerSignals;
        _loadBandSource = loadBandSource;

        // Cadence selection: when the adaptive trigger is enabled, subscribe to
        // the 1-second heartbeat so we can react to signal pressure quickly.
        // When it's disabled, stay on the legacy Tick1m cadence so callers
        // upgrading don't see additional ticks fire.
        if (scheduleCoordinator is not null)
        {
            var cadence = _options.Calibration.Trigger.Enabled ? TickCadence.Tick1s : TickCadence.Tick1m;
            _subscription = scheduleCoordinator.Subscribe(
                cadence,
                "IdentityWeightCalibrationService",
                CostHint.High,
                OnTickAsync);
        }
    }

    /// <summary>
    ///     Snapshot of the most recent trigger evaluation. Surfaced by the
    ///     forthcoming <c>/admin/learning/health</c> endpoint so operators can
    ///     answer "why isn't calibration firing" without re-reading the spec.
    /// </summary>
    public TriggerDecisionSnapshot LastDecisionSnapshot => _lastDecision;

    /// <summary>Public read of the volatile snapshot held by the calibration service.</summary>
    public sealed record TriggerDecisionSnapshot(
        DateTimeOffset At,
        bool ShouldRun,
        string Reason,
        IReadOnlyDictionary<string, double> Signals)
    {
        public static readonly TriggerDecisionSnapshot Empty = new(
            DateTimeOffset.MinValue, false, "never evaluated", new Dictionary<string, double>(0));
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

        // Adaptive gate (preferred): when configured, consult the policy on
        // every heartbeat. The policy itself enforces the min-interval floor,
        // so we can safely subscribe to Tick1s without the work running every
        // second.
        var triggerOpts = _options.Calibration.Trigger;
        if (triggerOpts.Enabled)
        {
            var signals = _triggerSignals?.Snapshot()
                          ?? Triggers.NullAdaptiveTriggerSignalSource.Instance.Snapshot();
            var band = _loadBandSource?.CurrentBand ?? Services.LoadBand.Low;
            var ctx = new Triggers.TriggerContext(
                Now: now,
                LastSuccessfulRunUtc: _lastSuccessfulRunUtc == DateTime.MinValue
                    ? null
                    : new DateTimeOffset(_lastSuccessfulRunUtc, TimeSpan.Zero),
                CurrentBand: band,
                Signals: signals);

            var policy = BuildPolicy(triggerOpts);
            var decision = Triggers.AdaptiveTriggerEvaluator.Evaluate(policy, ctx);
            _lastDecision = new TriggerDecisionSnapshot(now, decision.ShouldRun, decision.Reason, signals);

            if (!decision.ShouldRun) return;
        }
        else
        {
            // Legacy fixed-cadence gate. Identical semantics to the pre-Phase-1
            // behaviour so a host that doesn't enable the trigger sees no change.
            var interval = TimeSpan.FromMinutes(Math.Max(1, _options.Calibration.CalibrationIntervalMinutes));
            if (_lastSuccessfulRunUtc != DateTime.MinValue &&
                now.UtcDateTime - _lastSuccessfulRunUtc < interval)
            {
                return; // Not yet due.
            }
        }

        try
        {
            // Reset signals BEFORE the run so observations arriving during a
            // long calibration count toward the NEXT cycle, not this one.
            _triggerSignals?.Reset();

            await RunOnceAsync(ct);
            // Use the tick's clock, not DateTime.UtcNow, so virtual-clock tests
            // (and any future deterministic-replay harness) compute elapsed
            // against the same time basis the eval received.
            _lastSuccessfulRunUtc = now.UtcDateTime;
        }
        catch (OperationCanceledException) { return; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Calibration tick failed");
        }
    }

    private static Triggers.AdaptiveTriggerPolicy BuildPolicy(CalibrationTriggerOptions opts)
    {
        var conditions = new List<Triggers.SignalCondition>(2);
        if (opts.ObservationThreshold > 0)
            conditions.Add(new Triggers.SignalCondition(
                Triggers.CalibrationSignalSource.SignalKeys.ObsUnabsorbed,
                opts.ObservationThreshold));
        if (opts.DriftL2Threshold > 0)
            conditions.Add(new Triggers.SignalCondition(
                Triggers.CalibrationSignalSource.SignalKeys.DriftL2,
                opts.DriftL2Threshold));

        return new Triggers.AdaptiveTriggerPolicy
        {
            Enabled = opts.Enabled,
            MinInterval = opts.MinInterval,
            MaxInterval = opts.MaxInterval,
            MaxBand = opts.MaxBand,
            AnyOfSignals = conditions,
        };
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
        // Cold-seed pass: on the very first calibration run, INSERT every
        // registered archetype IF NOT ALREADY PRESENT so the durable
        // identity_archetypes table is populated with the full root taxonomy
        // before any traffic arrives. Critical: uses InsertArchetypeIfMissingAsync
        // (INSERT ... ON CONFLICT DO NOTHING) so refined centroids that the
        // calibration tick wrote in a previous process run survive restart.
        // The destructive UpsertArchetypeAsync path stays reserved for the
        // refinement loop below, which writes a centroid derived from real
        // descendants -- not the sparse synthetic centroid the cold-seed
        // carries from the YAML / BotPatterns / WellKnownBots synthesizer.
        if (Interlocked.CompareExchange(ref _initialColdSeedDone, 1, 0) == 0)
        {
            var seeded = 0;
            foreach (var archetype in _archetypes.All)
            {
                try
                {
                    await _store.InsertArchetypeIfMissingAsync(archetype, ct);
                    seeded++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Cold-seed insert failed for archetype {Id}; registry retains the in-memory copy",
                        archetype.ArchetypeId);
                }
            }
            _logger.LogInformation(
                "Cold-seed pass attempted {Count} insert-if-missing operations on identity_archetypes",
                seeded);
        }

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

        // Archetype self-refinement with Phase 4 adaptive alpha + Phase 5 v2
        // neighbour-aware repulsion + mobility tracking. Per archetype:
        //   1) Compute refined centroid with alpha chosen by the previous cycle's
        //      DescendantVarianceLastCycle (low var -> high alpha, high var ->
        //      low alpha, spec §2.D rule 1).
        //   2) If a previously-recorded nearest neighbour is dangerously close
        //      AND we're the smaller archetype, build a repulsion vector to push
        //      the refinement target away from it (spec §2.D rule 2).
        //   3) Track L2 between pre and post centroid -> CentroidDeltaLastCycle.
        //   4) Pin counter: increment when delta below threshold, reset on movement.
        //   5) Store mean variance for the NEXT cycle's alpha derivation.
        var refined = new List<IdentityArchetype>();
        var refinedCount = 0;
        var mobilityOpts = _options.Calibration.CentroidMobility;

        // Pre-build an archetype-id -> archetype lookup so repulsion can find
        // the neighbour by id without re-scanning the list O(n²) inside the
        // refinement loop.
        var archetypeById = new Dictionary<string, IdentityArchetype>(
            _archetypes.All.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var a in _archetypes.All) archetypeById[a.ArchetypeId] = a;

        foreach (var archetype in _archetypes.All)
        {
            var descendants = fingerprints
                .Where(fp => string.Equals(fp.InferredClientType, archetype.ArchetypeId,
                    StringComparison.OrdinalIgnoreCase))
                .Select(fp => fp.Centroid)
                .ToList();

            // Adaptive alpha derived from LAST cycle's descendant variance.
            // First-cycle archetypes have DescendantVarianceLastCycle == 0 so the
            // formula collapses to the alpha cap, identical to pre-Phase-4 behaviour.
            var adaptiveAlpha = IdentityWeightMath.AdaptiveAlphaFromVariance(
                alphaCap: _options.Calibration.ArchetypeRefinementCap,
                alphaMin: mobilityOpts.AlphaMin,
                meanVariance: archetype.DescendantVarianceLastCycle,
                varianceScale: mobilityOpts.VarianceScale);

            // Repulsion: look up the previously-recorded nearest neighbour and
            // build a push-away vector if conditions fire. Null when no neighbour,
            // distance large, this archetype dominates, or strength = 0.
            float[]? repulsion = null;
            if (archetype.NearestNeighbourId is not null
                && archetypeById.TryGetValue(archetype.NearestNeighbourId, out var neighbour))
            {
                repulsion = IdentityWeightMath.BuildRepulsionVector(
                    thisCentroid: archetype.Centroid,
                    neighbourCentroid: neighbour.Centroid,
                    thisDescendantCount: archetype.DescendantCount,
                    neighbourDescendantCount: neighbour.DescendantCount,
                    currentDistance: archetype.NearestNeighbourDistance,
                    repulsionRadius: mobilityOpts.RepulsionRadius,
                    repulsionStrength: mobilityOpts.RepulsionStrength);
                if (repulsion is not null)
                    _logger.LogInformation(
                        "Neighbour repulsion: archetype {Id} pushed away from {Neighbour} (dist {Dist:F3} < radius {Radius:F3})",
                        archetype.ArchetypeId, archetype.NearestNeighbourId,
                        archetype.NearestNeighbourDistance, mobilityOpts.RepulsionRadius);
            }

            var refinement = IdentityWeightMath.RefineArchetypeCentroidAdaptive(
                archetype.Centroid, descendants, adaptiveAlpha, repulsion);

            if (refinement is null)
            {
                // No descendants -> the archetype isn't being PINNED, it's
                // being IGNORED. Reset pin_cycles so the endpoint's
                // top_pinned view doesn't fill with archetypes that never
                // had work. Reset delta + variance for the same reason.
                refined.Add(archetype with
                {
                    CentroidDeltaLastCycle = 0,
                    PinCycles = 0,
                    DescendantVarianceLastCycle = 0,
                });
                continue;
            }

            var (newCentroid, delta, meanVariance) = refinement.Value;
            var pinCycles = delta <= mobilityOpts.PinDeltaThreshold
                ? archetype.PinCycles + 1
                : 0;

            var updated = archetype with
            {
                Centroid = newCentroid,
                DescendantCount = descendants.Count,
                LastRefinedAt = DateTime.UtcNow,
                CentroidDeltaLastCycle = delta,
                PinCycles = pinCycles,
                DescendantVarianceLastCycle = meanVariance,
            };
            refined.Add(updated);
            await _store.UpsertArchetypeAsync(updated, ct);
            refinedCount++;
        }

        // Phase 4 rule 3: nearest-neighbour distance per archetype. Pairwise L2
        // is O(n²) but n ~150 for the FOSS catalogue so the cost is negligible
        // (~22.5k ops per cycle). Observe-only in v1 -- we surface the proximity
        // but don't repel.
        refined = AnnotateNearestNeighbours(refined);

        // Phase 5 v3 (spec §2.D rule 3): convergence detection. Walk the
        // refined archetypes' nearest-neighbour annotations, bump counters
        // for pairs that stay close, surface a structured warning once a pair
        // persists for ConvergenceWarnAfterCycles consecutive cycles.
        _lastConvergenceCandidates = UpdateConvergenceCounters(
            refined, _options.Calibration.UmbrellaShrinkage, _convergenceCycleCounters);
        foreach (var c in _lastConvergenceCandidates)
        {
            if (c.Cycles == _options.Calibration.UmbrellaShrinkage.ConvergenceWarnAfterCycles)
                _logger.LogWarning(
                    "centroid.merge-candidate: {A} and {B} have stayed within {Dist:F3} for {Cycles} cycles -- consider merging or diverging the YAML seed",
                    c.ArchetypeA, c.ArchetypeB, c.Distance, c.Cycles);
        }

        if (refinedCount > 0)
        {
            _logger.LogInformation(
                "Refined {Count} archetypes from descendant fingerprint centroids", refinedCount);

            // Pin-cycle warning: if an archetype has been pinned for many
            // cycles AND has high descendant variance, it's stuck in the wrong
            // position with disagreeing descendants -- the exact "we're pinning
            // and never updating" scenario the maintainer flagged.
            foreach (var arch in refined)
            {
                if (arch.PinCycles >= mobilityOpts.PinWarnAfterCycles
                    && arch.DescendantVarianceLastCycle > mobilityOpts.HighVarianceThreshold)
                {
                    _logger.LogWarning(
                        "Centroid pinned: archetype {Id} stuck for {Cycles} cycles with descendant variance {Var:F4} -- consider manual centroid reseed or wait for umbrella shrinkage",
                        arch.ArchetypeId, arch.PinCycles, arch.DescendantVarianceLastCycle);
                }
            }
        }

        // Registry replace happens unconditionally inside the shrinkage step
        // below (it always uses the latest refined list as its input).

        // Phase 2: per-archetype, per-UA drift metrics. Computed against the
        // POST-refinement centroids (we want "how far have my descendants strayed
        // from where I just decided I sit" not "from where I used to sit"). The
        // archetype lookup uses the refined list so the centroid match is exact.
        // Defensive ?? empty-list because Moq-wrapped IFingerprintStore proxies
        // bypass the interface's default implementation and return null for
        // IReadOnlyList<T> setup-less methods.
        var driftRows = await _store.ListRecentObservationsForDriftAsync(maxRowsPerArchetype: 5000, ct)
            ?? Array.Empty<DriftObservationRow>();
        var metrics = ComputeDriftMetrics(driftRows, refined);
        if (metrics.Count > 0)
        {
            try
            {
                await _store.InsertDriftMetricsAsync(metrics, ct);
                _lastDriftMetrics = metrics;
                _logger.LogInformation(
                    "Calibration wrote {Count} archetype drift-metric rows", metrics.Count);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Drift metrics persist failed -- in-memory snapshot still updated for /admin/learning/health");
                _lastDriftMetrics = metrics;
            }
        }

        // Phase 3 + Phase 5: umbrella shrinkage + auto-regrowth. Walk the drift
        // metrics, apply the action ladder from spec §2.C per archetype, and
        // tighten the catchment via VarianceMultiplier on the in-memory archetype.
        // The matcher's MaskedSimilarityCore multiplies its per-dim variance by
        // this scalar -- below 1.0 means "I'm an over-claiming umbrella, penalise
        // deviation harder". Persisted via UpsertArchetypeAsync; surfaced on the
        // dashboard. Healthy cycles also walk multipliers back toward 1.0 so a
        // transient leakage doesn't permanently degrade the archetype.
        var (shrunken, shrinkActions) = ApplyUmbrellaShrinkage(refined, metrics,
            _options.Calibration.UmbrellaShrinkage);

        // ALWAYS replace the in-memory registry: even a quiet cycle bumps the
        // per-archetype HealthyCycles counter, which feeds the regrowth gate.
        // Without this propagation the counter would reset to its in-memory
        // value every cycle and regrowth would never fire.
        _archetypes.Replace(shrunken);
        _lastShrinkActions = shrinkActions;

        if (shrinkActions.Count > 0)
        {
            foreach (var archetype in shrunken)
            {
                if (!shrinkActions.ContainsKey(archetype.ArchetypeId)) continue;
                try
                {
                    await _store.UpsertArchetypeAsync(archetype, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Umbrella-action persist failed for {Id}; in-memory still updated",
                        archetype.ArchetypeId);
                }
            }
            foreach (var (id, action) in shrinkActions)
            {
                if (action.SplitCandidate)
                    _logger.LogWarning(
                        "Umbrella SPLIT-CANDIDATE: archetype {Id} bloat={Bloat:F2} leakage={Leak} -- consider splitting in YAML",
                        id, action.Bloat, action.LeakageDescendantCount);
                else if (action.Kind == "regrow")
                    _logger.LogInformation(
                        "Umbrella regrowth: archetype {Id} VarianceMultiplier {From:F3} -> {To:F3} (no leakage / bloat for {Healthy}+ cycles)",
                        id, action.PreviousMultiplier, action.NewMultiplier,
                        _options.Calibration.UmbrellaShrinkage.RegrowAfterCycles);
                else
                    _logger.LogInformation(
                        "Umbrella shrinkage: archetype {Id} VarianceMultiplier {From:F3} -> {To:F3} (bloat={Bloat:F2}, leakage={Leak})",
                        id, action.PreviousMultiplier, action.NewMultiplier,
                        action.Bloat, action.LeakageDescendantCount);
            }
        }

        return new CalibrationResult(
            FingerprintCount: fingerprints.Count,
            ClustersUsed: clustersUsed,
            WeightsComputed: weights is not null,
            ArchetypesRefined: refinedCount);
    }

    /// <summary>
    ///     Last cycle's shrinkage decisions keyed by archetype id. Surfaced on
    ///     the <c>/admin/learning/health</c> endpoint so operators see which
    ///     umbrellas the calibration is acting on. Replaced-not-mutated; safe
    ///     for concurrent reads.
    /// </summary>
    private volatile IReadOnlyDictionary<string, ShrinkageAction> _lastShrinkActions
        = new Dictionary<string, ShrinkageAction>();

    /// <summary>Snapshot of the most-recent shrinkage decisions. Empty before the first cycle.</summary>
    public IReadOnlyDictionary<string, ShrinkageAction> LastShrinkActions => _lastShrinkActions;

    /// <summary>
    ///     Phase 5 v3 (spec §2.D rule 3) convergence tracker. Keyed on the
    ///     canonical ordered pair "(a|b)" (a &lt; b lex) so the lookup is
    ///     deterministic regardless of iteration order. Value is the number of
    ///     consecutive calibration cycles the pair has stayed within
    ///     <see cref="UmbrellaShrinkageOptions.ConvergenceMergeThreshold"/>.
    ///     Cleared when the pair drifts back outside the threshold.
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, int>
        _convergenceCycleCounters = new(StringComparer.Ordinal);

    /// <summary>
    ///     Active merge-candidate pairs at the end of the most recent calibration
    ///     cycle. Exposed for the observability endpoint so operators can see
    ///     "this pair has been within X for Y cycles -- consider merging or
    ///     diverging the YAML seeds". Empty before the first cycle.
    /// </summary>
    public sealed record ConvergenceCandidate(
        string ArchetypeA,
        string ArchetypeB,
        double Distance,
        int Cycles);

    private volatile IReadOnlyList<ConvergenceCandidate> _lastConvergenceCandidates
        = Array.Empty<ConvergenceCandidate>();
    public IReadOnlyList<ConvergenceCandidate> LastConvergenceCandidates
        => _lastConvergenceCandidates;

    /// <summary>
    ///     Per-archetype outcome of one shrinkage pass. Surfaced on the
    ///     observability endpoint so operators can trace WHY an archetype's
    ///     catchment narrowed or widened this cycle.
    /// </summary>
    /// <param name="PreviousMultiplier">Variance multiplier before this cycle.</param>
    /// <param name="NewMultiplier">Variance multiplier after this cycle.</param>
    /// <param name="Bloat">Measured catchment bloat metric.</param>
    /// <param name="LeakageDescendantCount">Number of descendants flagged as leakage this cycle.</param>
    /// <param name="SplitCandidate"><c>true</c> when the archetype is flagged for split consideration.</param>
    /// <param name="Kind">"shrink", "regrow", or "split-candidate".</param>
    public sealed record ShrinkageAction(
        double PreviousMultiplier,
        double NewMultiplier,
        double Bloat,
        int LeakageDescendantCount,
        bool SplitCandidate,
        string Kind = "shrink");

    /// <summary>
    ///     Apply the spec §2.C action ladder. For each archetype, derive bloat
    ///     and leakage from <paramref name="metrics"/>; if either threshold
    ///     fires, multiply <see cref="IdentityArchetype.VarianceMultiplier"/>
    ///     by <c>(1 - ShrinkRate)</c> down to the floor. <paramref name="opts"/>
    ///     supplies the four knobs (bloat threshold, split threshold, shrink
    ///     rate, floor) so production can tune without re-deploying. Returns
    ///     the rewritten archetype list AND a per-id action map for telemetry.
    /// </summary>
    internal static (IReadOnlyList<IdentityArchetype> Archetypes,
                     IReadOnlyDictionary<string, ShrinkageAction> Actions)
        ApplyUmbrellaShrinkage(
            IReadOnlyList<IdentityArchetype> archetypes,
            IReadOnlyList<ArchetypeDriftMetric> metrics,
            UmbrellaShrinkageOptions opts)
    {
        // Bucket metrics by archetype: matching-UA rows vs leakage rows.
        // Missing-from-metrics archetypes still walk through the regrowth path
        // below (a quiet cycle counts toward HealthyCycles).
        var matchingP90 = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var leakageCount = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in metrics)
        {
            if (m.MatchesAssertedUa)
            {
                if (!matchingP90.TryGetValue(m.ArchetypeId, out var existing) || m.P90L2ToCentroid > existing)
                    matchingP90[m.ArchetypeId] = m.P90L2ToCentroid;
            }
            else
            {
                leakageCount[m.ArchetypeId] =
                    leakageCount.TryGetValue(m.ArchetypeId, out var c) ? c + m.DescendantCount : m.DescendantCount;
            }
        }

        var actions = new Dictionary<string, ShrinkageAction>(StringComparer.OrdinalIgnoreCase);
        var rewritten = new List<IdentityArchetype>(archetypes.Count);
        foreach (var arch in archetypes)
        {
            var p90 = matchingP90.TryGetValue(arch.ArchetypeId, out var pv) ? pv : 0.0;
            var leak = leakageCount.TryGetValue(arch.ArchetypeId, out var lv) ? lv : 0;
            var bloat = opts.RadiusBaseline > 0 ? p90 / opts.RadiusBaseline : 0.0;

            var shouldShrink = bloat >= opts.BloatShrinkThreshold || leak > 0;
            var splitCandidate = bloat >= opts.SplitCandidateThreshold;

            if (shouldShrink || splitCandidate)
            {
                // ---- SHRINK path ----
                var previous = arch.VarianceMultiplier;
                var next = Math.Max(opts.Floor, previous * (1.0 - opts.ShrinkRate));
                rewritten.Add(arch with
                {
                    VarianceMultiplier = next,
                    HealthyCycles = 0,    // any shrink resets the regrowth clock
                });
                actions[arch.ArchetypeId] = new ShrinkageAction(
                    PreviousMultiplier: previous,
                    NewMultiplier: next,
                    Bloat: bloat,
                    LeakageDescendantCount: leak,
                    SplitCandidate: splitCandidate,
                    Kind: splitCandidate ? "split-candidate" : "shrink");
            }
            else
            {
                // ---- HEALTHY path: regrowth ----
                // No leakage, no bloat. Bump the healthy-cycle counter. Once
                // we've stayed quiet long enough AND the multiplier is still
                // below 1.0 (i.e. previously shrunk), walk it back up.
                var healthyCycles = arch.HealthyCycles + 1;
                var shouldRegrow = healthyCycles >= opts.RegrowAfterCycles
                                   && arch.VarianceMultiplier < 1.0
                                   && opts.RegrowRate > 0;

                if (shouldRegrow)
                {
                    var previous = arch.VarianceMultiplier;
                    var next = Math.Min(1.0, previous * (1.0 + opts.RegrowRate));
                    rewritten.Add(arch with
                    {
                        VarianceMultiplier = next,
                        HealthyCycles = healthyCycles,
                    });
                    actions[arch.ArchetypeId] = new ShrinkageAction(
                        PreviousMultiplier: previous,
                        NewMultiplier: next,
                        Bloat: bloat,
                        LeakageDescendantCount: 0,
                        SplitCandidate: false,
                        Kind: "regrow");
                }
                else
                {
                    rewritten.Add(arch with { HealthyCycles = healthyCycles });
                }
            }
        }

        return (rewritten, actions);
    }

    /// <summary>
    ///     In-memory mirror of the most recent drift-metrics batch. Lets the
    ///     observability endpoint serve the snapshot without a SQLite round-trip
    ///     and keeps it readable even if the durable insert fails. Volatile +
    ///     replaced-not-mutated so a concurrent read returns a consistent batch.
    /// </summary>
    private volatile IReadOnlyList<ArchetypeDriftMetric> _lastDriftMetrics = Array.Empty<ArchetypeDriftMetric>();

    /// <summary>Snapshot of the most-recent calibration cycle's drift metrics. Empty before the first run.</summary>
    public IReadOnlyList<ArchetypeDriftMetric> LastDriftMetrics => _lastDriftMetrics;

    /// <summary>
    ///     Group observation rows by <c>(ArchetypeId, UaFamily)</c>, compute mean
    ///     / sample variance / p90 of L2 distance to the matching archetype's
    ///     centroid, and stamp <see cref="ArchetypeDriftMetric.MatchesAssertedUa"/>
    ///     against the archetype's <see cref="IdentityArchetype.AssertedUaFamily"/>.
    ///     Observations whose archetype isn't in the registry (rare race during
    ///     archetype removal) are dropped silently -- the dashboard would have no
    ///     centroid to compare against anyway.
    /// </summary>
    internal static IReadOnlyList<ArchetypeDriftMetric> ComputeDriftMetrics(
        IReadOnlyList<DriftObservationRow> rows,
        IReadOnlyList<IdentityArchetype> archetypes)
    {
        if (rows.Count == 0) return Array.Empty<ArchetypeDriftMetric>();

        var byId = new Dictionary<string, IdentityArchetype>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in archetypes) byId[a.ArchetypeId] = a;

        // Partition the rows by (archetype, ua) bucket. Compute L2 once per row;
        // bucket the distances; derive stats per bucket. Single linear pass.
        var buckets = new Dictionary<(string Archetype, string Ua), List<double>>();
        foreach (var row in rows)
        {
            if (!byId.TryGetValue(row.ArchetypeId, out var archetype)) continue;
            var d = L2Distance(row.ObservationVector, archetype.Centroid);
            if (double.IsNaN(d) || double.IsInfinity(d)) continue;
            var key = (row.ArchetypeId, row.UaFamily ?? string.Empty);
            if (!buckets.TryGetValue(key, out var list))
            {
                list = new List<double>(4);
                buckets[key] = list;
            }
            list.Add(d);
        }

        var calibratedAt = DateTime.UtcNow;
        var metrics = new List<ArchetypeDriftMetric>(buckets.Count);
        foreach (var ((archetypeId, ua), distances) in buckets)
        {
            var archetype = byId[archetypeId];
            distances.Sort();

            double mean = 0;
            foreach (var d in distances) mean += d;
            mean /= distances.Count;

            double sumSq = 0;
            foreach (var d in distances)
            {
                var diff = d - mean;
                sumSq += diff * diff;
            }
            var variance = distances.Count > 1 ? sumSq / (distances.Count - 1) : 0;

            // p90 with nearest-rank: ceil(0.9 * N) - 1 is the index into the
            // sorted distances. For a single-element bucket this is the only
            // element.
            var p90Index = Math.Max(0, (int)Math.Ceiling(0.9 * distances.Count) - 1);
            var p90 = distances[p90Index];

            var matchesUa = !string.IsNullOrEmpty(archetype.AssertedUaFamily)
                            && !string.IsNullOrEmpty(ua)
                            && string.Equals(ua, archetype.AssertedUaFamily, StringComparison.OrdinalIgnoreCase);

            metrics.Add(new ArchetypeDriftMetric
            {
                ArchetypeId = archetypeId,
                UaFamily = ua,
                MatchesAssertedUa = matchesUa,
                DescendantCount = distances.Count,
                MeanL2ToCentroid = mean,
                VarianceL2ToCentroid = variance,
                P90L2ToCentroid = p90,
                CalibratedAt = calibratedAt,
            });
        }
        return metrics;
    }

    private static double L2Distance(float[] a, float[] b)
    {
        if (a.Length != b.Length) return double.NaN;
        double sum = 0;
        for (var i = 0; i < a.Length; i++)
        {
            var d = (double)a[i] - b[i];
            sum += d * d;
        }
        return Math.Sqrt(sum);
    }

    /// <summary>
    ///     Update the convergence-cycle counters for archetype pairs that
    ///     sit within <see cref="UmbrellaShrinkageOptions.ConvergenceMergeThreshold"/>.
    ///     Counter rule:
    ///     <list type="bullet">
    ///         <item>Below threshold: counter += 1.</item>
    ///         <item>Above threshold OR pair not represented this cycle:
    ///         counter reset (removed from the map) so a noisy pair resets
    ///         cleanly.</item>
    ///     </list>
    ///     Returns the active candidates (counter &#x2265; warn threshold).
    ///     Static so it can be unit-tested without a full calibration loop.
    /// </summary>
    internal static IReadOnlyList<ConvergenceCandidate> UpdateConvergenceCounters(
        IReadOnlyList<IdentityArchetype> archetypes,
        UmbrellaShrinkageOptions opts,
        System.Collections.Concurrent.ConcurrentDictionary<string, int> counters)
    {
        if (opts.ConvergenceMergeThreshold <= 0 || opts.ConvergenceWarnAfterCycles <= 0)
            return Array.Empty<ConvergenceCandidate>();

        // Collect pairs that are close this cycle. Use the nearest-neighbour
        // annotation -- it's already computed and ensures we visit each pair
        // at most twice (once per direction); canonical key dedupes.
        var seenThisCycle = new HashSet<string>(StringComparer.Ordinal);
        var candidates = new List<ConvergenceCandidate>();
        foreach (var arch in archetypes)
        {
            if (arch.NearestNeighbourId is null) continue;
            if (arch.NearestNeighbourDistance > opts.ConvergenceMergeThreshold) continue;

            var key = CanonicalPairKey(arch.ArchetypeId, arch.NearestNeighbourId);
            if (!seenThisCycle.Add(key)) continue;

            var cycles = counters.AddOrUpdate(key, 1, (_, v) => v + 1);
            if (cycles >= opts.ConvergenceWarnAfterCycles)
            {
                // Canonical pair ordering for the surface so the dashboard row
                // is stable: A is always the lex-lower id.
                var (a, b) = arch.ArchetypeId.CompareTo(arch.NearestNeighbourId) < 0
                    ? (arch.ArchetypeId, arch.NearestNeighbourId)
                    : (arch.NearestNeighbourId, arch.ArchetypeId);
                candidates.Add(new ConvergenceCandidate(
                    ArchetypeA: a, ArchetypeB: b,
                    Distance: arch.NearestNeighbourDistance,
                    Cycles: cycles));
            }
        }

        // Drop any pair that wasn't close this cycle -- counter reset.
        foreach (var staleKey in counters.Keys.Where(k => !seenThisCycle.Contains(k)).ToArray())
            counters.TryRemove(staleKey, out _);

        return candidates;
    }

    private static string CanonicalPairKey(string a, string b)
        => string.CompareOrdinal(a, b) < 0 ? $"{a}|{b}" : $"{b}|{a}";

    /// <summary>
    ///     Compute pairwise L2 between every archetype centroid, then for each
    ///     archetype record the closest other archetype + distance. v1 is
    ///     observe-only -- we don't repel; we just surface the "moves closer
    ///     to others" signal on the observability endpoint so operators can
    ///     watch convergence happen. v2 may add active repulsion per spec §2.D
    ///     rule 2.
    /// </summary>
    internal static List<IdentityArchetype> AnnotateNearestNeighbours(
        IReadOnlyList<IdentityArchetype> archetypes)
    {
        var n = archetypes.Count;
        if (n < 2)
        {
            // No neighbours possible -- clear any stale annotation but keep
            // every other field as-is.
            var solo = new List<IdentityArchetype>(n);
            foreach (var a in archetypes)
                solo.Add(a with { NearestNeighbourId = null, NearestNeighbourDistance = 0 });
            return solo;
        }

        var bestDistance = new double[n];
        var bestIdx = new int[n];
        for (var i = 0; i < n; i++)
        {
            bestDistance[i] = double.MaxValue;
            bestIdx[i] = -1;
        }

        // Half-matrix walk: each pair updates both i and j slots so we touch
        // every pair exactly once (n*(n-1)/2 iterations).
        for (var i = 0; i < n; i++)
        {
            var ci = archetypes[i].Centroid;
            for (var j = i + 1; j < n; j++)
            {
                var d = L2Distance(ci, archetypes[j].Centroid);
                if (double.IsNaN(d)) continue;     // dimension mismatch (defensive)
                if (d < bestDistance[i]) { bestDistance[i] = d; bestIdx[i] = j; }
                if (d < bestDistance[j]) { bestDistance[j] = d; bestIdx[j] = i; }
            }
        }

        var result = new List<IdentityArchetype>(n);
        for (var i = 0; i < n; i++)
        {
            var a = archetypes[i];
            if (bestIdx[i] < 0)
            {
                result.Add(a with { NearestNeighbourId = null, NearestNeighbourDistance = 0 });
                continue;
            }
            result.Add(a with
            {
                NearestNeighbourId = archetypes[bestIdx[i]].ArchetypeId,
                NearestNeighbourDistance = bestDistance[i],
            });
        }
        return result;
    }
}

public sealed record CalibrationResult(
    int FingerprintCount,
    int ClustersUsed,
    bool WeightsComputed,
    int ArchetypesRefined);
