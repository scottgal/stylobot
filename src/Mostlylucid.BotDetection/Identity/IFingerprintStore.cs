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
    Task<IdentityCachedVerdict?> GetCachedVerdictForSignatureAsync(
        string primarySignature, CancellationToken ct = default);

    Task UpdateCachedVerdictAsync(
        string fingerprintId, double botProbability, string? riskBand, CancellationToken ct = default);

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

    // ── Test rig ─────────────────────────────────────────────────────────────
    Task<IReadOnlyDictionary<string, int>> TruncateAllAsync(CancellationToken ct = default);
}
