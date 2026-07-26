namespace Mostlylucid.BotDetection.Models;

/// <summary>
///     Configuration knobs for the metastable fingerprint match system.
///     See docs/architecture/fingerprint-match.md for design.
/// </summary>
public sealed class IdentityOptions
{
    /// <summary>
    ///     Master switch. When false, the IdentityVector and FingerprintMatch foundation
    ///     contributors do not run; PrimarySignature remains the identity key as before.
    ///     Defaults to false until the implementation is feature-complete.
    /// </summary>
    public bool Enabled { get; set; }

    public IdentityVectorOptions Vector { get; set; } = new();
    public IdentityMatchOptions Match { get; set; } = new();
    public IdentityWeightsOptions Weights { get; set; } = new();
    public IdentityDriftOptions Drift { get; set; } = new();
    public IdentityCalibrationOptions Calibration { get; set; } = new();
    public IdentityEngineOptions Engine { get; set; } = new();
    public IdentityCoordinatorOptions Coordinator { get; set; } = new();
    public IdentityLooksLikeOptions LooksLike { get; set; } = new();
    public BrowserModeOptions BrowserMode { get; set; } = new();
    public BrowserCharOptions BrowserChar { get; set; } = new();

    /// <summary>
    ///     When true (default), <see cref="Services.FingerprintNameComposer"/>
    ///     recomposes the display name on every matcher tick (lookup hit).
    ///     When false, the name is computed once at cold-start and reused.
    ///     Production leaves this on so a freshly-arrived bot-pattern match
    ///     replaces an earlier UA-family fallback. Spec D2 (deferred SignalSink
    ///     wiring — currently honored by direct invocation from the matcher).
    /// </summary>
    public bool NameRecomposeOnMatch { get; set; } = true;

    // ── Durable bounding (identity data guardians, Part B) ───────────────────

    /// <summary>
    ///     Most-recent absorbed observations the
    ///     <c>FingerprintObservationRetentionGuardian</c> keeps per fingerprint;
    ///     older absorbed rows are pruned. Unabsorbed rows are always kept.
    ///     The guardian floors the effective keep-count against
    ///     <see cref="DriftMaxRowsPerArchetype"/> regardless of this value, so
    ///     pruning never starves archetype drift.
    /// </summary>
    public int MaxObservationsPerFingerprint { get; set; } = 5_000;

    /// <summary>
    ///     Per-archetype row cap the drift reader
    ///     (<c>ListRecentObservationsForDriftAsync</c>) ranks over, and the floor
    ///     the observation-retention guardian keeps per fingerprint so a prune can
    ///     never delete a row the drift reader would rank (both readers scan
    ///     absorbed rows unfiltered). Single source of truth for both the drift
    ///     sampling depth and the retention floor: raise this and the guardian
    ///     keeps more; lower it (tests) and both move together.
    /// </summary>
    public int DriftMaxRowsPerArchetype { get; set; } = 5_000;

    /// <summary>
    ///     Ceiling on distinct fingerprints the <c>FingerprintEvictionGuardian</c>
    ///     enforces (memory-adaptive, ramps down under pressure). 0 disables the
    ///     cap (unlimited posture, guardian is a no-op). Default 50 000.
    /// </summary>
    public int MaxFingerprints { get; set; } = 50_000;

    /// <summary>
    ///     Floor the eviction cap ramps down to under memory pressure. Never
    ///     evicts below this many fingerprints. Default 2 000.
    /// </summary>
    public int MinFingerprints { get; set; } = 2_000;

    /// <summary>
    ///     Recency half-life fed to <see cref="Storage.DecisionNecessity.ColdnessScore"/>
    ///     for fingerprint eviction: a fingerprint's retention value halves every
    ///     half-life. Default 7 days.
    /// </summary>
    public TimeSpan FingerprintRecencyHalfLife { get; set; } = TimeSpan.FromDays(7);

    /// <summary>
    ///     Hard ceiling on distinct per-fingerprint dim snapshots held in the
    ///     in-memory <c>FingerprintDimSnapshotCache</c> (the drift baseline that
    ///     <c>IdentityChangeAtom</c> writes every identity-bearing request). The
    ///     cache actively evicts the LFU / expired tail on each write once over
    ///     this cap, so a rotated-fingerprint flood cannot grow it without bound
    ///     (the OOM-crash root cause). Default 50 000 -- matches the
    ///     <c>SqliteFingerprintStore</c> in-memory cap.
    /// </summary>
    public int FingerprintDimSnapshotCacheMaxSize { get; set; } = 50_000;

    /// <summary>
    ///     Lifetime of a per-fingerprint dim snapshot in
    ///     <c>FingerprintDimSnapshotCache</c>. A snapshot older than this is
    ///     evicted (drift is re-baselined from the next observation). Default 24h.
    /// </summary>
    public TimeSpan FingerprintDimSnapshotCacheTtl { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>
///     Per-request browser-mode classifier knobs. A browser mode is the
///     request-shape class a single browser is currently operating in — same
///     Chrome user emits navigation on a page load, xhr on API fetches,
///     sub-resource on stylesheets, signalr-negotiate on hub connects, etc.
///     The inventory lives in YAML (<c>Definitions/BrowserModes/*.yaml</c>);
///     these knobs only control the dispatch behaviour, not the predicate
///     library. See docs/architecture/composite-character-fingerprints.md.
/// </summary>
public sealed class BrowserModeOptions
{
    /// <summary>
    ///     Master switch. When false, the BrowserModeClassifierContributor does
    ///     not run; downstream consumers see <c>identity.browser_mode</c> as
    ///     absent. Defaults to true so step 1 (classifier-only, no persistence
    ///     change) is active wherever Identity.Enabled is also true.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Mode id emitted when no YAML predicate matches the request. The
    ///     string is the registry's lookup key for the fallback mode; the YAML
    ///     inventory should ship one entry with this id and an empty predicate
    ///     so the lookup is well-defined.
    /// </summary>
    public string FallbackModeId { get; set; } = "unknown";

    /// <summary>
    ///     Maximum unabsorbed mode observation rows the drainer fetches per
    ///     tick. Bounds the per-tick SQL statement size, the
    ///     <see cref="DateTime"/> parameter list size for SQLite's IN clause,
    ///     and the worst-case memory footprint of one tick's batch.
    ///     Conservative default keeps a busy gateway honest under bursty
    ///     traffic without saturating the absorption transaction.
    /// </summary>
    public int DrainMaxRowsPerTick { get; set; } = 5_000;

    /// <summary>
    ///     Composite spec step 4: parent fingerprint centroid becomes a
    ///     maturity-weighted mean of its child mode centroids, recomputed on
    ///     a tick. Default false initially so this lands as observable
    ///     infrastructure without disturbing identity stability — the parent
    ///     absorption path keeps owning the centroid until an operator (or
    ///     step 4b) flips the gate after validating the rollup math on live
    ///     traffic. When true, the rollup overwrites the parent centroid on
    ///     each tick; the parent-absorption-vs-rollup contention is tracked
    ///     in <c>project_absorption_services_migration</c>.
    /// </summary>
    public bool RollupEnabled { get; set; }

    /// <summary>
    ///     Recompute cadence for the rollup. 5 minutes by default matches the
    ///     spec's tick.5m subscription target. Hot fingerprints get caught
    ///     either way because the rollup sweeps oldest-mode-row-last-seen
    ///     first.
    /// </summary>
    public int RollupRecomputeIntervalSeconds { get; set; } = 300;

    /// <summary>
    ///     Maximum fingerprints the rollup sweep recomputes per tick. Bounds
    ///     each tick's work to a predictable upper limit on a busy gateway;
    ///     uncovered fingerprints get caught next tick because the sweep
    ///     orders by oldest-mode-row-last-seen.
    /// </summary>
    public int RollupMaxFingerprintsPerTick { get; set; } = 1_000;
}

/// <summary>
///     "Looks like" lookup on the signature detail page. The dashboard runs a vec0
///     KNN over <c>fingerprints_vec</c> (excluding self) when an operator opens a
///     visitor whose own archetype is generic / unknown, so they can see what
///     known shapes the centroid is closest to. The relationship is a calculation,
///     not a stored field: as the population evolves the neighbours drift, so the
///     lookup runs per page load against the current centroid set.
/// </summary>
public sealed class IdentityLooksLikeOptions
{
    /// <summary>Master switch for the signature-detail "Looks like" panel.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How many neighbours to surface. Operators usually want the single closest, but 3 reads as a small ranked list when curiosity > brevity.</summary>
    public int NeighbourCount { get; set; } = 3;

    /// <summary>
    ///     L2 distance ceiling. Matches farther than this are dropped — at extreme
    ///     distances the "looks like" answer is more confusing than useful (a
    ///     visitor's centroid can be near-equidistant from every archetype in a
    ///     sparse population). Set to a large number to never filter.
    /// </summary>
    public double MaxDistance { get; set; } = 4.0;
}

/// <summary>
///     Bounds and tuning for <c>IdentityProcessingCoordinator</c> — the slow-path queue
///     that runs Pass 2 / corrections / absorption / EWMA updates / on-demand drift
///     checks. The fast path (cache hits, L1 confirm wins) does not touch this
///     coordinator. Under burst, the coordinator sheds rather than blocking — callers
///     receive a "shed" outcome and use the fast-path default verdict.
/// </summary>
public sealed class IdentityCoordinatorOptions
{
    /// <summary>Global queue depth cap. Beyond this, oldest item is dropped to admit the new one.</summary>
    public int MaxQueueDepth { get; set; } = 10_000;

    /// <summary>Per-fingerprint cap on queued items. Burst from a single fp gets coalesced past this.</summary>
    public int MaxQueuedPerFingerprint { get; set; } = 4;

    /// <summary>
    ///     A new request finding an in-flight Pass 2 for the same fingerprint younger
    ///     than this joins as a waiter (coalesces); older than this, the new request is
    ///     shed and falls back to the fast-path default.
    /// </summary>
    public int CoalesceWindowMs { get; set; } = 100;

    /// <summary>Circuit breaker trips when global queue depth exceeds this fraction of <see cref="MaxQueueDepth"/>.</summary>
    public double BreakerTripThreshold { get; set; } = 0.80;

    /// <summary>Breaker auto-resets when global queue depth drops below this fraction.</summary>
    public double BreakerResetThreshold { get; set; } = 0.30;

    /// <summary>Breaker only trips after the trip-threshold condition has held this long.</summary>
    public int BreakerTripHoldSeconds { get; set; } = 5;

    /// <summary>Breaker only resets after the reset-threshold condition has held this long.</summary>
    public int BreakerResetHoldSeconds { get; set; } = 10;

    /// <summary>Aging boost applied per second a queued item waits — prevents starvation under sustained high-risk load.</summary>
    public double AgingBoostPerSecond { get; set; } = 0.01;

    /// <summary>
    ///     Worker pool size. The coordinator runs N independent workers pulling from the
    ///     priority queue; per-fp ordering is enforced by the inflight tracker (a worker
    ///     skips items whose fp is already executing). Higher counts give parallelism
    ///     across fingerprints — important for the manual AI opinion path which can
    ///     take seconds. 1 keeps the slow path strictly serial (deterministic but slow
    ///     under any blocking op).
    /// </summary>
    public int WorkerCount { get; set; } = 4;
}

public sealed class IdentityVectorOptions
{
    /// <summary>Absorb a detailed observation after the fingerprint sees N more requests.</summary>
    public int AbsorptionMaturityThreshold { get; set; } = 5;

    /// <summary>
    ///     Adaptive forgetting: sample which observations keep a detail row. When true
    ///     (default), a <i>confirmatory</i> observation (one whose vector barely moves an
    ///     already-matured fingerprint's centroid) is <b>summarised</b> rather than persisted:
    ///     the observation count and centroid maturity advance, but no
    ///     <c>fingerprint_observations</c> / <c>observations_vec</c> detail row is written and
    ///     no absorber wake fires. The detail table therefore grows with behavioural NOVELTY,
    ///     not request volume, so a high-cardinality flood of look-alike requests cannot balloon
    ///     the identity store. Observations that change the score (novelty at or above
    ///     <see cref="ObservationNoveltyKeepThreshold"/>) and every observation on a
    ///     still-maturing fingerprint (maturity below <see cref="AbsorptionMaturityThreshold"/>,
    ///     the identity-building phase) always keep full detail. Set false to persist every
    ///     observation (legacy behaviour).
    /// </summary>
    public bool AdaptiveObservationSampling { get; set; } = true;

    /// <summary>
    ///     Novelty floor for <see cref="AdaptiveObservationSampling"/>. An observation whose
    ///     cosine novelty (<c>1 - cosine-similarity</c> to the current fingerprint centroid) is
    ///     at or above this value keeps a full detail row; below it, the observation is
    ///     summarised. 0 keeps every observation (disables sampling for matured fingerprints);
    ///     higher values summarise more aggressively. Default 0.05: a 5% shape change is "novel
    ///     enough" to be worth a detail row for drift analysis. Kept well below the drift
    ///     detector's own thresholds so real drift is never sampled away
    ///     (feedback: FOSS never loses detection sensitivity).
    /// </summary>
    public double ObservationNoveltyKeepThreshold { get; set; } = 0.05;

    /// <summary>Absorb observations older than this on active fingerprints.</summary>
    public int AbsorptionAgeDays { get; set; } = 30;

    /// <summary>A fingerprint counts as active if it has received an observation in this window.</summary>
    public int ActiveWindowDays { get; set; } = 90;

    /// <summary>
    ///     Guardian throttle: the absorption catch-up (both the debounced fast-path fold and the
    ///     backstop sweep) evaluates at most this many fingerprints per run, ordered by
    ///     <c>last_seen DESC</c> (the DB corollary of the in-memory LFU). Just-observed fingerprints
    ///     sort to the top so the fast-path fold always finds the fingerprint that triggered it;
    ///     colder fingerprints with pending observations age in over successive backstop ticks.
    ///     Bounds the working-set scan + connection appetite under a high-cardinality flood.
    ///     Read by both <c>SqliteFingerprintStore</c> and the commercial mirror; not a store option.
    /// </summary>
    public int AbsorptionMaxFingerprintsPerRun { get; set; } = 256;

    /// <summary>
    ///     Fraction of L1-confirmed requests for which an observation is recorded. 1.0 = every
    ///     request; lower values sample on very hot fingerprints (CDN warm pools, etc.).
    ///     Slow-path detectors always run regardless; this only gates the observation-row write
    ///     and the eventual centroid update.
    /// </summary>
    public double ObservationSamplingRate { get; set; } = 1.0;

    /// <summary>
    ///     Observation-count thresholds the matcher announces via
    ///     <see cref="SignalKeys.IdentityFingerprintObservationCountCrossed"/>. Async absorption
    ///     fires on each crossing instead of polling on a fixed interval.
    ///     Default: 1, 3, 10, 30, 100. Empty array disables emission.
    /// </summary>
    public int[] NotifyOnCountCrossings { get; set; } = new[] { 1, 3, 10, 30, 100 };

    /// <summary>
    ///     Per-fingerprint debounce window for the <see cref="Mostlylucid.BotDetection.Identity.IFingerprintStore.ObservationAppended"/>
    ///     subscription. A second event for the same fingerprint within this window collapses
    ///     into a single absorption run. Prevents storms when a hot visitor floods observations
    ///     faster than absorption can drain. Default 250ms.
    /// </summary>
    public int SubscriptionDebounceMs { get; set; } = 250;

    /// <summary>
    ///     Safety-net backstop sweep cadence for absorption. Subscribes to ObservationAppended
    ///     for the hot path; this loop catches any fingerprints missed by the subscription
    ///     (event handler failure, crash recovery, late SQLite rows). Default 300s (5min).
    ///     Was 5s when the loop was the only mechanism (driven by Drift.DriftCheckIntervalSeconds).
    /// </summary>
    public int BackstopSweepIntervalSeconds { get; set; } = 300;

    /// <summary>
    ///     Amortise the per-request encoder cost across sub-resource requests in a page load.
    ///     When enabled (default), <see cref="Mostlylucid.BotDetection.Identity.EncoderResultCache"/> short-circuits the encode
    ///     step for any request whose <c>primary_signature</c> was seen within the TTL window.
    ///     For a 30-asset page load this collapses 30 × ~1 μs of encode to one encode plus
    ///     29 cache lookups. Disable on hosts that need per-request encode fidelity (e.g.
    ///     volatile session.* dims must be exact for downstream consumers).
    /// </summary>
    public bool EncoderCacheEnabled { get; set; } = true;

    /// <summary>
    ///     Cache entry lifetime. 1 second comfortably covers a page-load asset burst (most
    ///     browsers complete sub-resource fetches within ~500 ms of the document arriving)
    ///     while limiting how stale the volatile session.* dims (request_rate, path_entropy,
    ///     session_age) can get on the cached vector. Larger windows trade freshness for
    ///     hit-rate.
    /// </summary>
    public int EncoderCacheTtlMs { get; set; } = 1_000;

    /// <summary>
    ///     Maximum live cache entries. Bounds memory: each entry holds one ~352-byte vector
    ///     plus the dictionary slot. 10k entries ≈ 5 MB upper bound — comfortably small for
    ///     a busy gateway.
    /// </summary>
    public int EncoderCacheMaxEntries { get; set; } = 10_000;
}

public sealed class IdentityMatchOptions
{
    /// <summary>Weighted-cosine score required for a confident match.</summary>
    public double MergeThreshold { get; set; } = 0.92;

    /// <summary>Below this score, allocate a new fingerprint instead of matching.</summary>
    public double LooseThreshold { get; set; } = 0.75;

    /// <summary>Top-K candidates pulled per vec0 query before re-ranking.</summary>
    public int TopK { get; set; } = 10;

    /// <summary>Number of dims listed in identity.rotation_dimensions when in the rotation band.</summary>
    public int RotationDimensionsTopK { get; set; } = 5;

    /// <summary>Minimum width-normalised top-slot score required before the matcher writes the drift-top-slot signals. Below this, a small float-noise drift would name every fingerprint as "drifted". Default 0.05.</summary>
    public double DriftEpsilon { get; set; } = 0.05;

    /// <summary>
    ///     Drift threshold that triggers a display-name recompute + persist on a matched
    ///     fingerprint. Names are stable across requests; only when drift exceeds this
    ///     significantly-higher bar (default 0.20, 4x DriftEpsilon) does the matcher
    ///     regenerate the name and write it to the fingerprint row. Float noise must not
    ///     move names.
    /// </summary>
    public double SignificantDriftEpsilon { get; set; } = 0.20;

    /// <summary>
    ///     Minimum unique fingerprints per cluster before <c>BotClusterService</c>
    ///     reseats their <c>root_centroid</c> to the cluster's community mean.
    ///     A 1-member "community" would replace the fingerprint's root with its
    ///     own centroid, leaving drift permanently at zero. Default 2 -- raise on
    ///     deployments with very noisy clusters to require stronger evidence
    ///     before adapting the reference centroid.
    /// </summary>
    public int RootReseatMinClusterFingerprints { get; set; } = 2;

    /// <summary>
    ///     Maximum number of <c>fingerprint_root_history</c> rows the dashboard
    ///     timeline displays per fingerprint, newest-first. Each row is a past
    ///     reference centroid + lineage marker. Default 12 = enough to see the
    ///     archetype-seed origin plus ~10 cluster reseats.
    /// </summary>
    public int RootHistoryDisplayLimit { get; set; } = 12;

    /// <summary>
    ///     Minimum archetype-match cosine score required before
    ///     <c>FingerprintMatchContributor.WriteArchetypeSignals</c> emits the
    ///     <c>identity.archetype_name</c> / drift signals. Below this floor the signals are
    ///     suppressed and the name composer falls through to UA family + OS — preventing
    ///     sparse vectors from being mis-labelled as the closest-by-noise archetype (e.g. a
    ///     real Chrome visitor coming through with a partial fingerprint being named
    ///     "Headless Chrome" because that archetype's centroid happened to be nearest).
    ///
    ///     <para>
    ///     Default 0.0 (disabled). Raising to ~0.5 yields more honest naming but interacts
    ///     unpredictably with the BDF synthetic-context rig (one chrome-windows scenario
    ///     loses a fingerprint id at threshold > 0), pending investigation. The gate itself
    ///     is wired so operators can tune it for their environment; the production sidecar
    ///     gets the same default as the FOSS library.
    ///     </para>
    /// </summary>
    public double MinArchetypeMatchScore { get; set; } = 0.0;
}

public sealed class IdentityWeightsOptions
{
    /// <summary>Per-fingerprint weight signal 1: corrections (sharp edits when L1 was wrong).</summary>
    public double CorrectionLearningRate { get; set; } = 0.05;

    /// <summary>Per-fingerprint weight signal 2: stability (gentler, every absorption).</summary>
    public double StabilityLearningRate { get; set; } = 0.01;

    /// <summary>Numeric stability lower bound on per-dimension weights. Not a data cap.</summary>
    public double MinWeight { get; set; } = 0.1;

    /// <summary>Numeric stability upper bound on per-dimension weights. Not a data cap.</summary>
    public double MaxWeight { get; set; } = 10.0;

    /// <summary>How often the matcher rechecks identity_dimension_weights.last_computed_at.</summary>
    public int GlobalRefreshSeconds { get; set; } = 60;
}

public sealed class IdentityDriftOptions
{
    /// <summary>FingerprintDriftService tick interval. Drift surfaces within this many seconds.</summary>
    public int DriftCheckIntervalSeconds { get; set; } = 5;

    /// <summary>Maximum sampled observations Pass 2 re-verifies per drift tick.</summary>
    public int DriftBatchSize { get; set; } = 50;

    /// <summary>Fraction of L1-confirmed requests sampled into the drift-verification queue.</summary>
    public double DriftSamplingRate { get; set; } = 0.05;

    /// <summary>EWMA alpha for cached_bot_probability updates from in-line classifier verdicts.</summary>
    public double CachedScoreEwmaAlpha { get; set; } = 0.2;

    /// <summary>
    ///     A fingerprint's cached score (and the L1-confirmed verdict it represents) is considered
    ///     fresh for this many seconds. The drift service re-verifies fingerprints whose
    ///     cached_score_updated_at is null or older than this, so a "passes as human" L1 hit cannot
    ///     persist indefinitely without the L2 vector match agreeing.
    /// </summary>
    public int CachedScoreTtlSeconds { get; set; } = 60;

    /// <summary>
    ///     Weighted-cosine score below which the drift service flags a fingerprint as drifting.
    ///     Defaults to MergeThreshold so any L2 score that wouldn't have confirmed in-line counts
    ///     as drift; tighter values (e.g. 0.85) reduce noise on slowly-shifting fingerprints.
    /// </summary>
    public double DriftWarningThreshold { get; set; } = 0.92;

    /// <summary>
    ///     EWMA alpha for the per-fingerprint ambiguity-persistence signal. Each match
    ///     outcome bumps the EWMA: ambiguity events (Pass 2 correction, rotation candidate,
    ///     L1 confirm fail, new fingerprint allocation) push toward 1; clean L1 confirm
    ///     successes push toward 0. The smoothed value reveals fingerprints that
    ///     persistently live in the boundary band — rare for legit traffic, near-diagnostic
    ///     for adversarial probing of the gate semantics.
    /// </summary>
    public double AmbiguityEwmaAlpha { get; set; } = 0.1;

    /// <summary>
    ///     Above this <c>ambiguity_persistence</c> value, a fingerprint is flagged as
    ///     boundary-probing. Emits <c>identity.ambiguity_probing = true</c> as a positive
    ///     bot signal in its own right. Default 0.4 — a fingerprint that triggers slow
    ///     path on >40% of recent requests is doing something legitimate clients don't.
    /// </summary>
    public double AmbiguityProbingThreshold { get; set; } = 0.4;

    /// <summary>
    ///     Cosine delta past which the dashboard row shows the "drifted" badge
    ///     and the signature detail page renders the drift detail panel. Lower
    ///     than <see cref="IdentityMatchOptions.SignificantDriftEpsilon"/> on
    ///     purpose — the operator-visible warning fires before the friendly-pin
    ///     revocation. Default 0.15. See
    ///     docs/superpowers/specs/2026-06-22-identity-mode-archetype-name-design.md.
    /// </summary>
    public double DriftBadgeThreshold { get; set; } = 0.15;
}

public sealed class IdentityCalibrationOptions
{
    /// <summary>
    ///     Legacy fixed-cadence gate. Retained for backwards compatibility:
    ///     when <see cref="Trigger"/> is left at its disabled default, this
    ///     value drives the original wall-clock behaviour. When
    ///     <see cref="CalibrationTriggerOptions.Enabled"/> is true, this field
    ///     is ignored and the adaptive trigger decides.
    /// </summary>
    public int CalibrationIntervalMinutes { get; set; } = 30;

    /// <summary>Maximum α (descendant blend ratio) in archetype self-refinement.</summary>
    public double ArchetypeRefinementCap { get; set; } = 0.7;

    /// <summary>
    ///     Adaptive trigger policy. When <see cref="CalibrationTriggerOptions.Enabled"/>
    ///     is true the calibration service consults
    ///     <see cref="Mostlylucid.BotDetection.Identity.Triggers.AdaptiveTriggerEvaluator"/> on every Tick1s
    ///     heartbeat instead of gating on <see cref="CalibrationIntervalMinutes"/>.
    ///     Demo profiles set the floor as low as 1 s so learning is visible
    ///     within seconds; production defaults are conservative.
    /// </summary>
    public CalibrationTriggerOptions Trigger { get; set; } = new();

    /// <summary>
    ///     Phase 3 umbrella-shrinkage knobs. Calibration narrows the catchment
    ///     of archetypes whose drift metrics show leakage (descendants whose
    ///     UA disagrees with what the archetype asserts) or excessive bloat
    ///     (matching descendants drifting far from the centroid). See spec §2.C.
    /// </summary>
    public UmbrellaShrinkageOptions UmbrellaShrinkage { get; set; } = new();

    /// <summary>
    ///     Phase 4 centroid-mobility knobs (spec §2.D). Governs how aggressively
    ///     refinement moves the centroid based on descendant variance and how
    ///     long "pinned-with-disagreement" goes before raising a warning.
    /// </summary>
    public CentroidMobilityOptions CentroidMobility { get; set; } = new();
}

/// <summary>
///     Knobs for the Phase 4 centroid-mobility rules. v1 is observe-only on
///     neighbour proximity (no repulsion); the adaptive-&#x3B1; rule is active.
/// </summary>
public sealed class CentroidMobilityOptions
{
    /// <summary>
    ///     Minimum &#x3B1; the adaptive rule will drop to when descendant variance
    ///     is high. Default 0.2 -- below this the refinement effectively pauses
    ///     and umbrella shrinkage carries the cleanup load.
    /// </summary>
    public double AlphaMin { get; set; } = 0.2;

    /// <summary>
    ///     Variance scale in the adaptive-&#x3B1; formula
    ///     <c>&#x3B1; = &#x3B1;Cap * exp(-meanVariance / VarianceScale)</c>.
    ///     Lower scale = &#x3B1; drops faster as variance rises. Default 0.1; the
    ///     identity vector is L2-normalised so per-dim variance is small in
    ///     absolute terms.
    /// </summary>
    public double VarianceScale { get; set; } = 0.1;

    /// <summary>
    ///     L2 delta below which a refinement counts as "no movement". Default
    ///     0.001 (effectively pin). Drives the
    ///     <see cref="Mostlylucid.BotDetection.Identity.IdentityArchetype.PinCycles"/> counter.
    /// </summary>
    public double PinDeltaThreshold { get; set; } = 0.001;

    /// <summary>
    ///     Consecutive pinned cycles before the calibration logs a warning.
    ///     Default 5 -- the maintainer's "we're pinning and never updating"
    ///     concern manifests visibly only after several cycles of no movement.
    /// </summary>
    public int PinWarnAfterCycles { get; set; } = 5;

    /// <summary>
    ///     Threshold on
    ///     <see cref="Mostlylucid.BotDetection.Identity.IdentityArchetype.DescendantVarianceLastCycle"/> above
    ///     which we consider descendants to be disagreeing. Pin + high variance
    ///     together raise the warning. Default 0.05.
    /// </summary>
    public double HighVarianceThreshold { get; set; } = 0.05;

    /// <summary>
    ///     Phase 5 v2 (2026-06-21): active neighbour-aware repulsion. When the
    ///     archetype currently being refined sits within
    ///     <see cref="RepulsionRadius"/> of its
    ///     <see cref="Mostlylucid.BotDetection.Identity.IdentityArchetype.NearestNeighbourId"/> AND has fewer
    ///     descendants (it's the weaker one being eaten by the umbrella), the
    ///     refinement target is pushed AWAY from the neighbour by
    ///     <see cref="RepulsionStrength"/> of a unit vector. Without this,
    ///     two converging archetypes silently merge -- the exact "moves closer
    ///     to others" concern. Defaults are conservative; set
    ///     <see cref="RepulsionStrength"/> to 0 to disable repulsion entirely
    ///     and fall back to observe-only neighbour proximity.
    /// </summary>
    public double RepulsionRadius { get; set; } = 0.3;

    /// <summary>
    ///     L2 magnitude of the per-cycle repulsion vector applied to the
    ///     refinement target when the conditions in <see cref="RepulsionRadius"/>
    ///     are met. Default 0.02 -- intentionally small to avoid oscillation
    ///     with adaptive &#x3B1; doing its own dampening.
    /// </summary>
    public double RepulsionStrength { get; set; } = 0.02;
}

/// <summary>
///     Knobs for the Phase 3 umbrella-shrinkage action ladder. The defaults
///     are conservative -- a stable archetype with no leakage doesn't shrink.
///     See <c>docs/superpowers/specs/2026-06-21-adaptive-learning-loop-design.md</c>
///     §2.C for the rationale on each knob.
/// </summary>
public sealed class UmbrellaShrinkageOptions
{
    /// <summary>
    ///     Reference L2 distance treated as "normal catchment". Bloat is
    ///     computed as <c>p90_drift / RadiusBaseline</c>. Default 0.5 -- the
    ///     identity vector is L2-normalised so 0.5 is a sensible mid-range
    ///     distance, tunable per-deployment as observability data accrues.
    /// </summary>
    public double RadiusBaseline { get; set; } = 0.5;

    /// <summary>Bloat threshold at which shrinkage fires. Default 1.0.</summary>
    public double BloatShrinkThreshold { get; set; } = 1.0;

    /// <summary>
    ///     Bloat threshold at which the calibration logs a
    ///     <c>umbrella.split-candidate</c> signal. Splitting is operator-driven
    ///     in v1 (the YAML author edits the catalogue); the signal makes the
    ///     decision visible. Default 2.0.
    /// </summary>
    public double SplitCandidateThreshold { get; set; } = 2.0;

    /// <summary>
    ///     Multiplicative shrink rate per cycle. <c>VarianceMultiplier_new
    ///     = VarianceMultiplier_prev * (1 - ShrinkRate)</c>. Default 0.05
    ///     (5%) -- balances responsiveness against oscillation under the
    ///     adaptive trigger's fast heartbeat.
    /// </summary>
    public double ShrinkRate { get; set; } = 0.05;

    /// <summary>
    ///     Floor for <c>VarianceMultiplier</c>. Prevents a perpetually-confused
    ///     archetype from vanishing entirely. Default 0.05 = "down to 5 % of
    ///     the original tolerance".
    /// </summary>
    public double Floor { get; set; } = 0.05;

    /// <summary>
    ///     Phase 5 auto-regrowth (2026-06-21). Consecutive healthy cycles (no
    ///     leakage, bloat below threshold) before the calibration starts
    ///     unwinding a previous shrinkage. Default 5 -- balances "react to a
    ///     transient population shift" against "don't yo-yo on momentary
    ///     quiet". Set to <see cref="int.MaxValue"/> to disable regrowth.
    /// </summary>
    public int RegrowAfterCycles { get; set; } = 5;

    /// <summary>
    ///     Per-cycle multiplicative regrowth rate. <c>VarianceMultiplier_new
    ///     = min(1.0, VarianceMultiplier_prev * (1 + RegrowRate))</c>.
    ///     Default 0.02 -- deliberately slower than <see cref="ShrinkRate"/>
    ///     so a noisy population can't trigger oscillation by alternating
    ///     leakage / quiet cycles.
    /// </summary>
    public double RegrowRate { get; set; } = 0.02;

    /// <summary>
    ///     Phase 5 v3 (2026-06-21) spec §2.D rule 3: convergence detection.
    ///     L2 distance at or below this is "close enough to be a merge
    ///     candidate". Pairs that stay close for
    ///     <see cref="ConvergenceWarnAfterCycles"/> consecutive cycles raise a
    ///     <c>centroid.merge-candidate</c> warning. Default 0.15 -- tighter
    ///     than <see cref="CentroidMobilityOptions.RepulsionRadius"/> so we
    ///     only warn on pairs that are genuinely converging despite Phase 5 v2
    ///     repulsion.
    /// </summary>
    public double ConvergenceMergeThreshold { get; set; } = 0.15;

    /// <summary>
    ///     Consecutive cycles a pair must stay below
    ///     <see cref="ConvergenceMergeThreshold"/> before the warning fires.
    ///     Default 3 -- enough to filter momentary noise while still surfacing
    ///     a real convergence within a single demo session.
    /// </summary>
    public int ConvergenceWarnAfterCycles { get; set; } = 3;
}

/// <summary>
///     Configuration mirror of <see cref="Mostlylucid.BotDetection.Identity.Triggers.AdaptiveTriggerPolicy"/>
///     suitable for binding from <c>appsettings.json</c>. Kept distinct from
///     the policy record so the bound shape stays open to extension
///     (additional axes, custom signal-key aliases) without forcing every
///     consumer to construct the immutable policy by hand.
/// </summary>
public sealed class CalibrationTriggerOptions
{
    /// <summary>
    ///     When false, the calibration service falls back to the legacy
    ///     <see cref="IdentityCalibrationOptions.CalibrationIntervalMinutes"/>
    ///     gate. Default false so the change is opt-in per environment.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    ///     Floor between calibration runs. Below this elapsed time we never
    ///     fire, regardless of signal pressure. Default 30 s.
    /// </summary>
    public TimeSpan MinInterval { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    ///     Safety net: once elapsed reaches this, the trigger fires even with
    ///     no signal pressure. Null to disable the safety net (signal-only).
    ///     Default 6 h.
    /// </summary>
    public TimeSpan? MaxInterval { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    ///     Highest <see cref="Services.LoadBand"/> we'll still calibrate at.
    ///     Default <see cref="Services.LoadBand.Normal"/> (skip when High or
    ///     Critical). Demo overrides may push this to Critical so demos still
    ///     learn under synthetic load.
    /// </summary>
    public Services.LoadBand MaxBand { get; set; } = Services.LoadBand.Normal;

    /// <summary>
    ///     Observation-count threshold. The trigger fires when this many
    ///     observations have been recorded since the last successful run.
    ///     Default 50 -- balances "react to bursts" against "don't recalibrate
    ///     for a handful of obs". Set to 1 in Demo profiles for instant
    ///     feedback. Set to 0 to disable this axis.
    /// </summary>
    public long ObservationThreshold { get; set; } = 50;

    /// <summary>
    ///     Drift-L2 sum threshold. The trigger fires when accumulated
    ///     per-fingerprint centroid movement crosses this value. Default 0.5
    ///     -- captures "the population is shifting" without firing on noise.
    ///     Set to 0 to disable this axis.
    /// </summary>
    public double DriftL2Threshold { get; set; } = 0.5;
}

public sealed class IdentityEngineOptions
{
    /// <summary>Prefer sqlite-vec when the extension loads; fall back to brute-force UDF when it cannot.</summary>
    public bool PreferSqliteVec { get; set; } = true;

    /// <summary>
    ///     Optional override for the sqlite-vec extension path. When null, the store calls
    ///     <c>conn.LoadExtension("vec0")</c> and SQLite resolves the binary from the OS
    ///     library search path (PATH on Windows, LD_LIBRARY_PATH on Linux, DYLD_LIBRARY_PATH
    ///     on macOS). Set to an absolute path (e.g. <c>/usr/local/lib/vec0.dylib</c>) when
    ///     the binary lives somewhere the search path doesn't cover.
    ///
    ///     Get the binary from https://github.com/asg017/sqlite-vec/releases (no in-tree
    ///     native dependency, so the brute-force engine remains the FOSS default and
    ///     operators opt into the perf path by installing the extension themselves).
    /// </summary>
    public string? SqliteVecExtensionPath { get; set; }

    /// <summary>
    ///     EWMA smoothing factor for the fingerprint's cached bot probability. Each
    ///     request-path verdict blends in at <c>blend = old * (1 - alpha) + new * alpha</c>.
    ///     Default 0.3 favours stability over single-request swings. Higher values track
    ///     the latest request more closely; lower values resist noise. The very first
    ///     write to a fingerprint is a direct assignment regardless of alpha.
    /// </summary>
    public double VerdictEwmaAlpha { get; set; } = 0.3;
}

/// <summary>
///     Browser-characteristic consistency (claim verification): score a session's
///     observed feature/engine vector against the learned browser_char centroid for
///     its CLAIMED {family}:{mode}. Deviation raises suspicion (never a human discount).
///     Runs only when Identity.Enabled AND a client beacon carried engine data.
/// </summary>
public sealed class BrowserCharOptions
{
    /// <summary>Master switch (default true so it is active wherever Identity.Enabled is).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    ///     Weight on the un-spoofable ENGINE dims in the browser_char cosine mask. High so
    ///     the score is anchored on the engine substrate a spoofer cannot fake.
    /// </summary>
    public float EngineWeight { get; set; } = 3.0f;

    /// <summary>
    ///     Weight on the spoofable / version-dependent FEATURE-presence dims. Low so a
    ///     minor-version lag or feature spoof cannot swing the verdict.
    /// </summary>
    public float FeatureWeight { get; set; } = 0.5f;

    /// <summary>
    ///     Drift (1 - weighted cosine, halved) above which the branch emits a suspicion
    ///     contribution. Below it, consistency is neutral (no human discount).
    /// </summary>
    public double DriftThreshold { get; set; } = 0.5;

    /// <summary>Contribution weight when the drift branch fires.</summary>
    public double Weight { get; set; } = 1.5;
}
