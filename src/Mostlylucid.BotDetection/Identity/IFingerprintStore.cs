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

    /// <summary>
    ///     Raised after a successful <see cref="RecordObservationAsync"/> durable write
    ///     commits. The string is the fingerprint id whose <c>observation_count</c> just
    ///     incremented. Subscribers (FingerprintAbsorptionService in Task 4) wake on this
    ///     event instead of polling the fingerprints table. Invocation is synchronous on
    ///     the call-site thread; subscribers must not block.
    /// </summary>
    event Action<string>? ObservationAppended;

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

    Task RecordObservationAsync(
        string fingerprintId,
        float[] vector,
        string? uaFamily = null,
        CancellationToken ct = default);

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

    // ── Trust state (claim verification) ────────────────────────────────────
    /// <summary>
    ///     Write-behind verifier hook: records the outcome of a successful
    ///     verifier run (rDNS, NodeInfo, IP-range, forward DNS, ...) onto the
    ///     fingerprint's persistent trust columns so future requests for the
    ///     same fingerprint can short-circuit re-verification while within
    ///     <c>TrustOptions.TrustCacheTtl</c>. Dict-authoritative LFU façade:
    ///     mutates the in-memory <c>_fingerprintById</c> entry first so the next
    ///     L1 read sees the new state immediately, then persists to the durable
    ///     store. Per <c>feedback_write_behind_lfu_facade</c>: never a
    ///     synchronous DB write on the request hot path; if the SQL throws,
    ///     the in-memory state is still consistent.
    ///     <para>
    ///     <paramref name="claimStatus"/> values: <c>verified</c>, <c>spoofed</c>,
    ///     <c>behaviourally-trusted</c>. Pass <c>unverified</c> to reset the
    ///     trust state. <paramref name="verificationMethod"/> records the path
    ///     that decided it (<c>ip_range</c>, <c>fcrdns</c>, <c>nodeinfo</c>,
    ///     <c>forward_dns</c>, <c>behavioural-trust</c>, ...).
    ///     <paramref name="verifiedAt"/> is the timestamp of the verification;
    ///     pass <c>null</c> on <c>unverified</c> / <c>spoofed</c> resets.
    ///     </para>
    /// </summary>
    Task UpdateClaimVerificationAsync(
        string fingerprintId,
        string claimStatus,
        string? verificationMethod,
        DateTime? verifiedAt,
        CancellationToken ct = default);

    // ── Display name ─────────────────────────────────────────────────────────
    Task UpdateDisplayNameAsync(
        string fingerprintId, string displayName, DateTime updatedAt,
        CancellationToken ct = default,
        string source = "matcher");

    Task<int> CountByDisplayNameAsync(string displayName, CancellationToken ct = default);

    Task UpdateDisplayNameForSignatureAsync(
        string primarySignature, string displayName, DateTime updatedAt,
        CancellationToken ct = default,
        string source = "matcher");

    /// <summary>
    ///     Bulk transparent-LFU read for view rendering: signature -> current display
    ///     name. Composes the two existing LFU dicts (_fingerprintIdByPrimarySig +
    ///     _fingerprintById) so the dashboard's per-row name lookup costs nothing on
    ///     a hot cache. Misses fall through to one bulk SQL roundtrip that populates
    ///     both dicts. Returned dictionary has an entry per input signature; value is
    ///     null when no fingerprint has been allocated yet for the signature.
    ///     <para>
    ///     This is THE read path for dashboard views per the LFU contract -- there is
    ///     no SignatureAggregate.BotName field; views ALWAYS go through this method
    ///     so the name has exactly one source of truth (Fingerprint.DisplayName).
    ///     </para>
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetDisplayNamesBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures, CancellationToken ct = default);

    /// <summary>
    ///     Snapshot read for the signature timeline view: returns the per-fingerprint
    ///     name-change history (old_name, new_name, source, changed_at) in reverse
    ///     chronological order. NOT cached -- snapshots are cold (timeline view is a
    ///     specific operator drill-in, not a per-request render), so this goes direct
    ///     to DB. Empty list when the fingerprint has no recorded name changes.
    /// </summary>
    Task<IReadOnlyList<DisplayNameChange>> GetDisplayNameHistoryAsync(
        string fingerprintId, int limit = 50, CancellationToken ct = default);

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

    /// <summary>
    ///     Cold-seed an archetype only if no row with the same archetype_id already
    ///     exists. Used by <see cref="IdentityWeightCalibrationService"/>'s
    ///     first-tick cold-seed pass to populate the durable table with the full
    ///     root taxonomy at startup WITHOUT overwriting refined centroids that
    ///     accumulated in previous process runs. Default implementation falls back
    ///     to <see cref="UpsertArchetypeAsync"/> for stores that don't yet
    ///     distinguish "insert-only" -- those stores keep the destructive cold-seed
    ///     semantic but at least nothing else breaks. Concrete stores (Sqlite,
    ///     PostgreSQL) override with <c>INSERT ... ON CONFLICT DO NOTHING</c>.
    /// </summary>
    Task InsertArchetypeIfMissingAsync(IdentityArchetype archetype, CancellationToken ct = default)
        => UpsertArchetypeAsync(archetype, ct);

    /// <summary>
    ///     Persist a batch of per-archetype drift metrics computed by a single
    ///     calibration pass. Idempotent on <c>(archetype_id, ua_family,
    ///     calibrated_at)</c> -- a re-run with the same timestamp replaces the
    ///     existing rows. See <see cref="ArchetypeDriftMetric"/> for the
    ///     interpretation of each field. Default no-op so older store impls
    ///     keep compiling without surfacing observability rows.
    /// </summary>
    Task InsertDriftMetricsAsync(
        IReadOnlyList<ArchetypeDriftMetric> metrics,
        CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>
    ///     Latest-cycle drift metrics for the observability endpoint. Returns
    ///     rows from the most-recent <c>calibrated_at</c> only -- callers
    ///     wanting a history walk should query the durable table directly.
    ///     Default empty so the endpoint degrades gracefully when no store
    ///     supports the surface yet.
    /// </summary>
    Task<IReadOnlyList<ArchetypeDriftMetric>> ListLatestDriftMetricsAsync(
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<ArchetypeDriftMetric>>(Array.Empty<ArchetypeDriftMetric>());

    /// <summary>
    ///     Pull recent observations with their fingerprint's inferred archetype
    ///     and the observation's UA family. Feeds the calibration pass's drift
    ///     metric computation: rows are grouped by <c>(archetype_id, ua_family)</c>
    ///     in-process. <paramref name="maxRowsPerArchetype"/> caps the scan so
    ///     large catalogues stay bounded -- pass int.MaxValue for no cap.
    ///     Default returns an empty list so older stores keep compiling.
    /// </summary>
    Task<IReadOnlyList<DriftObservationRow>> ListRecentObservationsForDriftAsync(
        int maxRowsPerArchetype,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<DriftObservationRow>>(Array.Empty<DriftObservationRow>());

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
