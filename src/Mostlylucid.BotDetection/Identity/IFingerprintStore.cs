namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Full read + write fingerprint store surface consumed by the detection
///     pipeline (matcher, absorption, drift, calibration, AI-opinion) and the
///     BDF replay rig. Extends <see cref="IFingerprintReader"/> with the write
///     and batch surface so commercial gateways can swap the concrete
///     <see cref="SqliteFingerprintStore"/> for a Postgres-backed implementation
///     without every consumer taking a hard dependency on the concrete type.
///
///     Sqlite-vec-specific members (<c>IsVecAvailable</c>) stay on the concrete
///     <see cref="SqliteFingerprintStore"/> because only the FOSS vec0 anchor
///     index needs them; the commercial anchor index resolves the concrete
///     Postgres store directly.
/// </summary>
public interface IFingerprintStore : IFingerprintReader
{
    /// <summary>The vector layout this store was bootstrapped against.</summary>
    IdentityVectorLayout Layout { get; }

    /// <summary>Idempotent schema bootstrap; safe to call on every operation.</summary>
    Task EnsureInitialisedAsync(CancellationToken ct = default);

    // ── Verdict cache ────────────────────────────────────────────────────────
    Task UpdateCachedVerdictAsync(
        string fingerprintId, double botProbability, string? riskBand, CancellationToken ct = default);

    /// <summary>
    ///     Request-path verdict write. EWMA-blends <paramref name="botProbability"/> with
    ///     the fingerprint's existing <c>cached_bot_probability</c> (or assigns directly
    ///     when no prior write exists), writes through the in-memory dict so the next L1
    ///     verdict lookup sees the new value immediately, and persists to SQLite for
    ///     restart-survival. The very first write is a direct assignment; subsequent
    ///     writes blend at <c>VerdictEwmaAlpha</c>.
    ///
    ///     Distinct from <see cref="UpdateCachedVerdictAsync"/>: that method is for the
    ///     manual operator AI-opinion path (direct overwrite, authoritative, evicts the
    ///     dict entry on completion). This method is for every detection's verdict
    ///     (smoothed, dict-authoritative, no eviction).
    /// </summary>
    Task RecordVerdictAsync(
        string fingerprintId,
        double botProbability,
        string? riskBand,
        CancellationToken ct = default);

    Task BumpCachedScoreCheckedAtAsync(string fingerprintId, CancellationToken ct = default);

    // ── Matcher write path ───────────────────────────────────────────────────
    Task InsertFingerprintAsync(Fingerprint fp, string primarySignature, CancellationToken ct = default);

    Task UpsertKeyAsync(string primarySignature, string fingerprintId, CancellationToken ct = default);

    Task RecordObservationAsync(string fingerprintId, float[] vector, CancellationToken ct = default);

    Task RecordCorrectionAsync(
        string requestId,
        string primarySignature,
        string? pass1FingerprintId,
        string pass2FingerprintId,
        float[] differentiator,
        float[] updatedPass2Weights,
        CancellationToken ct = default);

    Task AbsorbObservationAsync(
        long observationId,
        string fingerprintId,
        float[] newCentroid,
        int newMaturity,
        float[] newWeights,
        string? newInferredClientType = null,
        double newInferredTypeConfidence = 0,
        bool inferredTypeChanged = false,
        CancellationToken ct = default);

    Task<double> BumpAmbiguityPersistenceAsync(
        string fingerprintId, bool isAmbiguityEvent, double alpha, CancellationToken ct = default);

    /// <summary>
    ///     Overwrite the fingerprint's centroid + centroid_maturity with the
    ///     rollup the <c>FingerprintRollupRecomputeService</c> computed as the
    ///     maturity-weighted mean of its child mode centroids. Does not touch
    ///     weights / inferred client type / cached score — those are owned by
    ///     other paths. Invalidates the LFU fingerprint cache slot so the next
    ///     read sees the new state.
    /// </summary>
    Task UpdateRollupCentroidAsync(
        string fingerprintId,
        float[] newCentroid,
        int newMaturity,
        CancellationToken ct = default);

    // ── Display name ─────────────────────────────────────────────────────────
    Task UpdateDisplayNameAsync(
        string fingerprintId, string displayName, DateTime updatedAt, CancellationToken ct = default);

    Task<int> CountByDisplayNameAsync(string displayName, CancellationToken ct = default);

    Task UpdateDisplayNameForSignatureAsync(
        string primarySignature, string displayName, DateTime updatedAt, CancellationToken ct = default);

    // ── Batch read / drift / absorption picker ───────────────────────────────
    Task<IReadOnlyDictionary<string, float[]>> GetCentroidsBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures, CancellationToken ct = default);

    Task<IReadOnlyList<(string FingerprintId, float[] Vector)>> ListActiveObservationsAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<Fingerprint>> ListStaleScoreFingerprintsAsync(
        int ttlSeconds, int batchSize, CancellationToken ct = default);

    Task<float[]?> GetLatestObservationVectorAsync(string fingerprintId, CancellationToken ct = default);

    Task<IReadOnlyList<AbsorbableObservation>> ListAbsorbableObservationsAsync(
        int maturityThreshold, int ageDays, int activeWindowDays, CancellationToken ct = default);

    // ── Calibration ──────────────────────────────────────────────────────────
    Task UpsertGlobalWeightsAsync(
        float[] weights, int samplesUsed, int clustersUsed, int archetypesUsed,
        CancellationToken ct = default);

    Task<(float[] Weights, DateTime LastComputedAt)?> GetGlobalWeightsAsync(CancellationToken ct = default);

    Task UpsertArchetypeAsync(IdentityArchetype archetype, CancellationToken ct = default);

    // ── Vec KNN ──────────────────────────────────────────────────────────────
    Task<IReadOnlyList<(string FingerprintId, double Distance)>> SearchVecCentroidsAsync(
        float[] queryVector, int k, CancellationToken ct = default);

    Task<IReadOnlyList<(string FingerprintId, double Distance)>> SearchVecObservationsAsync(
        float[] queryVector, int k, CancellationToken ct = default);

    // ── Cluster-driven root reseat (adaptation loop) ─────────────────────────
    /// <summary>
    ///     Apply a fresh <c>BotClusterService</c> snapshot to the fingerprint roots.
    ///     For each cluster: resolve member signatures → fingerprint ids (via
    ///     <c>fingerprint_keys</c>), dedupe, load each member's live centroid, take
    ///     the mean, then in one transaction supersede each member's active
    ///     <c>fingerprint_root_history</c> row, insert a new active row with
    ///     <c>root_source = "cluster:&lt;id&gt;"</c>, and update each
    ///     fingerprint's <c>root_centroid</c> / <c>root_centroid_at</c> /
    ///     <c>root_source</c> in lockstep. This closes the adaptation loop: a
    ///     Chrome-142 release that shifts the population's centroids gets
    ///     reflected in every member fingerprint's reference within one
    ///     clustering cycle even when the seed archetype YAML is now stale.
    ///
    ///     Clusters with fewer than <paramref name="minMemberFingerprints"/>
    ///     unique fingerprints are skipped -- a 1-member "community" would just
    ///     replace the fingerprint's root with its own centroid (drift = 0
    ///     forever). Idempotent: safe to call repeatedly with the same input.
    /// </summary>
    Task ReseatRootCentroidsAsync(
        IReadOnlyCollection<ClusterRootUpdate> updates,
        int minMemberFingerprints = 2,
        CancellationToken ct = default);

    // ── Test rig ─────────────────────────────────────────────────────────────
    Task<IReadOnlyDictionary<string, int>> TruncateAllAsync(CancellationToken ct = default);
}

/// <summary>
///     One cluster's contribution to a root-reseat batch.
///     <see cref="MemberSignatures"/> is the cluster's signature set as produced by
///     <c>BotClusterService</c>; the store resolves them to fingerprint ids
///     internally so callers don't pay an extra round-trip per signature.
/// </summary>
public sealed record ClusterRootUpdate(
    string ClusterId,
    IReadOnlyCollection<string> MemberSignatures,
    int MemberCount);
