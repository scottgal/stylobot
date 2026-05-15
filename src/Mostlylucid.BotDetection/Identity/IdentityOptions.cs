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

    /// <summary>Absorb observations older than this on active fingerprints.</summary>
    public int AbsorptionAgeDays { get; set; } = 30;

    /// <summary>A fingerprint counts as active if it has received an observation in this window.</summary>
    public int ActiveWindowDays { get; set; } = 90;

    /// <summary>
    ///     Fraction of L1-confirmed requests for which an observation is recorded. 1.0 = every
    ///     request; lower values sample on very hot fingerprints (CDN warm pools, etc.).
    ///     Slow-path detectors always run regardless; this only gates the observation-row write
    ///     and the eventual centroid update.
    /// </summary>
    public double ObservationSamplingRate { get; set; } = 1.0;
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
}

public sealed class IdentityCalibrationOptions
{
    /// <summary>IdentityWeightCalibrationService run cadence.</summary>
    public int CalibrationIntervalMinutes { get; set; } = 30;

    /// <summary>Maximum α (descendant blend ratio) in archetype self-refinement.</summary>
    public double ArchetypeRefinementCap { get; set; } = 0.7;
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
    ///     Get the binary from https://github.com/asg017/sqlite-vec/releases — there is no
    ///     in-tree native dependency, so the brute-force engine remains the FOSS default
    ///     and operators opt into the perf path by installing the extension themselves.
    /// </summary>
    public string? SqliteVecExtensionPath { get; set; }
}
