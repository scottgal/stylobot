using Mostlylucid.BotDetection.Domains;

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

    // ── Ephemeral surface-dim drift lookback (#16) ───────────────────────────
    /// <summary>
    ///     Reads the last-seen <see cref="SurfaceDims"/> for a fingerprint from the
    ///     in-memory hot cache, or null if the fingerprint is not resident (or has no
    ///     dims recorded yet). Never touches SQLite — dims are ephemeral, in-memory only.
    ///     <see cref="Orchestration.Atoms.IdentityChangeAtom"/> reads its prior-dims
    ///     comparison baseline from here. Default no-op for stores that don't hold a
    ///     per-fingerprint hot cache (Null / remote-reader).
    /// </summary>
    SurfaceDims? GetLastSeenDims(string fingerprintId) => null;

    /// <summary>
    ///     Records the last-seen <see cref="SurfaceDims"/> for a fingerprint on its
    ///     resident hot-cache entry, co-indexed with the fingerprint and co-evicted with
    ///     it. NEVER persisted. Skips fingerprints that are not resident — a rotated
    ///     fingerprint we no longer cache re-baselines rather than growing an unbounded
    ///     shadow accumulator (the #16 leak). Default no-op for non-caching stores.
    /// </summary>
    void SetLastSeenDims(string fingerprintId, SurfaceDims dims) { }

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
        CancellationToken ct = default,
        string? botType = null);

    /// <summary>
    ///     Hot-path variant of <see cref="RecordVerdictAsync"/>: blends the per-request
    ///     probability into the in-memory fingerprint (dict-first) and persists via write-behind,
    ///     opening NO connection on the caller thread, so detection can record every request's
    ///     verdict without a per-request DB connection. The risk band is derived from the blended
    ///     score inside the store, never supplied by the caller, so it stays consistent with the
    ///     probability. Optional <paramref name="botType"/> is the CATALOGUE bot type
    ///     (Internal / SearchEngine / AiBot / Tool / GoodBot / ...) cached alongside the score
    ///     so the dashboard reads the real catalogue vocabulary through the LFU; null preserves
    ///     any existing stored type. Default no-op for stores that hold no resident dict (the
    ///     null store); SQLite + the commercial store override it.
    /// </summary>
    void RecordVerdictWriteBehind(string fingerprintId, double botProbability, string? botType = null) { }

    Task BumpCachedScoreCheckedAtAsync(string fingerprintId, CancellationToken ct = default);

    // ── Matcher write path ───────────────────────────────────────────────────
    Task InsertFingerprintAsync(Fingerprint fp, string primarySignature, CancellationToken ct = default);

    Task UpsertKeyAsync(string primarySignature, string fingerprintId, CancellationToken ct = default);

    /// <summary>
    ///     Append an unabsorbed observation row for <paramref name="fingerprintId"/>.
    ///     The <paramref name="scope"/> parameter tags the row with the
    ///     <c>(domain, host)</c> owner it was observed under so downstream
    ///     multi-domain aggregates can partition observations per-site.
    ///     Callers on the request hot path derive the scope from
    ///     <c>HttpContext.Items[HttpContextItemKeys.RequestScope]</c>;
    ///     background / seed / test callers pass <see cref="RequestScope.Unknown"/>.
    /// </summary>
    Task RecordObservationAsync(
        RequestScope scope,
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

    // ── Name slots ──────────────────────────────────────────────────────────
    /// <summary>Matcher writeback. High-frequency, write-behind LFU façade.</summary>
    Task UpdateInducedNameAsync(
        string fingerprintId,
        string inducedName,
        DateTime updatedAt,
        CancellationToken ct,
        string? signalSnapshotJson = null);

    /// <summary>LLM coordinator writeback. Medium-frequency, write-behind LFU façade.</summary>
    Task UpdateLlmNameAsync(
        string fingerprintId,
        string llmName,
        string? description,
        DateTime evaluatedAt,
        CancellationToken ct);

    /// <summary>Operator edit. Low-frequency, synchronous LFU+DB write.</summary>
    Task UpdateGivenNameAsync(
        string fingerprintId,
        string? givenName,
        string operatorId,
        DateTime updatedAt,
        CancellationToken ct);

    Task<int> CountByDisplayNameAsync(string displayName, CancellationToken ct = default);

    /// <summary>
    ///     Same shape as <see cref="CountByDisplayNameAsync"/> but excludes one
    ///     fingerprint from the count. Used by the matcher's recompose path so
    ///     "I currently hold this name" doesn't read as a collision: a count
    ///     of 1 where the only row is me means rename is safe; anything &gt; 0
    ///     after exclusion means another fingerprint genuinely owns the name
    ///     and the recompose must apply a distinctive modifier.
    /// </summary>
    Task<int> CountByDisplayNameExcludingFingerprintAsync(
        string displayName, string excludedFingerprintId, CancellationToken ct = default);

    /// <summary>
    ///     Count of name writes rejected by the contract gate. Non-zero means
    ///     some upstream caller (LLM callback, operator label, legacy import)
    ///     tried to write a shape the contract disallows; the name was
    ///     normalised to <c>Unknown &lt;hex&gt;</c>. Observable so the
    ///     rejection rate can be metered.
    /// </summary>
    long BannedShapeRejectionsCount { get; }

    /// <summary>
    ///     Bulk LFU read used by the dashboard. Returns the resolved
    ///     <c>given ?? llm ?? induced</c> for each primary signature.
    /// </summary>
    Task<IReadOnlyDictionary<string, string?>> GetResolvedNamesBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures,
        CancellationToken ct);

    /// <summary>
    ///     Bulk LFU read used by the dashboard for the per-signature score/verdict
    ///     scalars (probability, risk band, bot type, confidence, threat, is-bot,
    ///     verified). Mirrors <see cref="GetResolvedNamesBySignaturesAsync"/>: the
    ///     fingerprint LFU is the single source for these values and the dashboard's
    ///     <c>SignatureAggregateCache</c> reads them THROUGH this method at projection
    ///     time instead of caching a stale parallel copy per aggregate row. Never
    ///     touches the DB; reads the in-memory LFU map only. Signatures with no
    ///     resident fingerprint are omitted from the result (the caller treats a
    ///     missing entry as "not resolved yet" and falls back to 0/null defaults,
    ///     the same as names return null until resolved). Default empty so read-only
    ///     proxies and the null store (Identity:Enabled = false) opt out cleanly;
    ///     only <see cref="SqliteFingerprintStore"/> (and the commercial store)
    ///     override it.
    ///     <para>
    ///     TODO(commercial): <c>PostgreSQLFingerprintStore</c> in the
    ///     stylobot-commercial repo must add the matching override — walk its
    ///     resident fingerprint dict (signature → fingerprint) and map each to a
    ///     <see cref="ResolvedVerdict"/> exactly as the SQLite store does. It lives
    ///     in the other repo, so it is NOT touched here.
    ///     </para>
    /// </summary>
    Task<IReadOnlyDictionary<string, ResolvedVerdict>> GetResolvedVerdictsBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures,
        CancellationToken ct)
        => Task.FromResult<IReadOnlyDictionary<string, ResolvedVerdict>>(
            new Dictionary<string, ResolvedVerdict>(StringComparer.Ordinal));

    /// <summary>
    ///     Atom-walk enumeration for the LLM picker: returns hot fingerprints
    ///     whose induced has drifted since the last LLM eval (or never).
    ///     Never touches the DB; reads the in-memory LFU map only.
    /// </summary>
    IReadOnlyList<Fingerprint> EnumerateLlmRepickCandidates(int maxCount);

    /// <summary>
    ///     Snapshot read for the signature timeline view: returns the per-fingerprint
    ///     name-change history (old_name, new_name, source, changed_at) in reverse
    ///     chronological order. NOT cached -- snapshots are cold (timeline view is a
    ///     specific operator drill-in, not a per-request render), so this goes direct
    ///     to DB. Empty list when the fingerprint has no recorded name changes.
    /// </summary>
    Task<IReadOnlyList<DisplayNameChange>> GetDisplayNameHistoryAsync(
        string fingerprintId, int limit = 50, CancellationToken ct = default);

    /// <summary>
    ///     Walks the in-memory LFU map for fingerprints whose resolved name
    ///     (<see cref="FingerprintNameResolver.Resolve"/>) contains the search
    ///     term (case-insensitive substring). Returns up to maxResults; never
    ///     opens a DB connection. Sorted by LastSeen descending.
    /// </summary>
    Task<IReadOnlyList<FingerprintSearchHit>> SearchByResolvedNameAsync(
        string term, int maxResults, CancellationToken ct);

    // ── Batch read / drift / absorption picker ───────────────────────────────
    Task<IReadOnlyDictionary<string, float[]>> GetCentroidsBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures, CancellationToken ct = default);

    Task<IReadOnlyList<(string FingerprintId, float[] Vector)>> ListActiveObservationsAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<Fingerprint>> ListStaleScoreFingerprintsAsync(
        int ttlSeconds, int batchSize, CancellationToken ct = default);

    Task<float[]?> GetLatestObservationVectorAsync(string fingerprintId, CancellationToken ct = default);

    /// <summary>
    ///     Lists absorbable observations for at most <paramref name="maxFingerprints"/> fingerprints,
    ///     ordered by <c>last_seen DESC</c> (the just-observed fingerprint is always in-set; colder
    ///     ones age in over successive backstop ticks). Bounds the working-set scan under a
    ///     high-cardinality flood. Pass <c>IdentityVectorOptions.AbsorptionMaxFingerprintsPerRun</c>.
    /// </summary>
    Task<IReadOnlyList<AbsorbableObservation>> ListAbsorbableObservationsAsync(
        int maturityThreshold, int ageDays, int activeWindowDays, int maxFingerprints, CancellationToken ct = default);

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
    ///     Snapshot of every row in <c>identity_archetypes</c> matching the
    ///     given <paramref name="catalogueKind"/> discriminator. Used by mode
    ///     classification (kind: <c>browser_mode</c>) and identity archetype
    ///     loading (kind: <c>identity</c>) without duplicating the underlying
    ///     table or calibration infrastructure. Default returns an empty list
    ///     so older store impls keep compiling; concrete stores (Sqlite,
    ///     PostgreSQL) filter on <c>catalogue_kind = @kind</c>.
    /// </summary>
    Task<IReadOnlyList<IdentityArchetypeRow>> GetByCatalogueKindAsync(
        string catalogueKind, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<IdentityArchetypeRow>>(Array.Empty<IdentityArchetypeRow>());

    /// <summary>
    ///     Upsert one centroid row into <c>identity_archetypes</c>. Used by both
    ///     the identity-archetype calibration pass (kind: <c>identity</c>) and
    ///     the mode-centroid catalogue (kind: <c>browser_mode</c>). Idempotent
    ///     on <paramref name="archetypeId"/>. Unlike
    ///     <see cref="UpsertArchetypeAsync"/> this write does not require a
    ///     fully-populated <see cref="IdentityArchetype"/> record -- it carries
    ///     only the persisted columns the mode-centroid catalogue actually
    ///     drives (id, kind, centroid, maturity). Other columns either default
    ///     in the DB (<c>catalogue_kind</c>) or are populated on first insert
    ///     with sensible neutrals (name = id, archetype_kind = catalogue_kind,
    ///     descendant_count = 0) so the row is valid for the existing
    ///     calibration / drift / read paths.
    ///     Default no-op so older store impls keep compiling without
    ///     this column-level surface; concrete stores (Sqlite, PostgreSQL)
    ///     override with the matching UPSERT.
    /// </summary>
    Task UpsertCentroidAsync(
        string archetypeId,
        string catalogueKind,
        float[] centroid,
        double maturity,
        CancellationToken ct = default)
        => Task.CompletedTask;

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

    // ── Durable bounding (identity data guardians, Part B) ───────────────────
    // Default no-ops so read-only proxies (RemoteFingerprintReader) and the
    // null store (Identity:Enabled = false) opt out cleanly; only
    // SqliteFingerprintStore overrides them.

    /// <summary>Total distinct fingerprints currently stored (for cap enforcement).</summary>
    Task<int> GetFingerprintCountAsync(CancellationToken ct = default) => Task.FromResult(0);

    /// <summary>
    ///     Priority metadata for up to <paramref name="limit"/> fingerprints,
    ///     oldest-first (<c>last_seen ASC</c>), for the eviction guardian's
    ///     <see cref="Storage.DecisionNecessity"/> ranking. Oldest-first because
    ///     stale fingerprints are the likeliest eviction candidates; the guardian
    ///     scores the returned set and evicts the lowest-value ones.
    /// </summary>
    Task<IReadOnlyList<FingerprintPriorityInfo>> GetAllFingerprintPriorityInfoAsync(
        int limit, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<FingerprintPriorityInfo>>(Array.Empty<FingerprintPriorityInfo>());

    /// <summary>
    ///     Cascade-delete the given fingerprints and every per-fingerprint row they
    ///     own (observations, vec mirrors, keys, corrections, name / root history)
    ///     in one transaction. Returns the number of <c>fingerprints</c> rows removed.
    /// </summary>
    Task<int> DeleteFingerprintsAsync(
        IReadOnlyList<string> fingerprintIds, CancellationToken ct = default)
        => Task.FromResult(0);

    /// <summary>
    ///     Prune absorbed <c>fingerprint_observations</c> rows so each fingerprint
    ///     keeps at most <paramref name="keepPerFingerprint"/> of its most-recent
    ///     (by id) absorbed rows; all unabsorbed rows survive. Also drops the matching
    ///     <c>observations_vec</c> mirror rows. Returns the number of observation rows
    ///     pruned. Preserves the drift readers, which scan absorbed rows with no
    ///     <c>absorbed_at</c> filter -- callers MUST pass a keep-count at or above the
    ///     drift reader's per-archetype cap.
    /// </summary>
    Task<int> PruneAbsorbedObservationsAsync(
        int keepPerFingerprint, CancellationToken ct = default)
        => Task.FromResult(0);

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

/// <summary>
///     One match from <see cref="IFingerprintStore.SearchByResolvedNameAsync"/>.
///     <see cref="PrimarySignature"/> is the binding key the dashboard uses to
///     navigate to the signature detail surface; <see cref="ResolvedName"/> is
///     the <c>given ?? llm ?? induced</c> projection that matched the term.
/// </summary>
public sealed record FingerprintSearchHit(
    string FingerprintId,
    string PrimarySignature,
    string ResolvedName,
    DateTime LastSeen);
