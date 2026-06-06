namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Ephemeral-mode no-op fingerprint store. Reads return empty / null, writes
///     are dropped. The metastable identity layer is effectively disabled: every
///     visitor presents as a fresh fingerprint, no centroids or weights survive
///     a restart, and the absorption / drift / calibration hosted services run
///     against an empty dataset (and harmlessly do nothing each tick).
///
///     Used by <see cref="Extensions.ServiceCollectionExtensions.AddBotDetectionInMemory"/>.
///     If <c>Identity:Enabled = false</c> (the FOSS default), most of these
///     methods are never called at all -- they exist so the service registration
///     can still be satisfied without dragging SQLite into the process.
/// </summary>
public sealed class NullFingerprintStore : IFingerprintStore
{
    private static readonly IReadOnlyDictionary<string, float[]> _emptyCentroids
        = new Dictionary<string, float[]>();
    private static readonly IReadOnlyDictionary<string, int> _emptyCounts
        = new Dictionary<string, int>();

    public IdentityVectorLayout Layout { get; } = IdentityVectorLayout.DefaultV1();

#pragma warning disable CS0067 // Event is never used; null store never raises it.
    public event Action<string>? ObservationAppended;
#pragma warning restore CS0067

    public Task EnsureInitialisedAsync(CancellationToken ct = default) => Task.CompletedTask;

    // ── IFingerprintReader ───────────────────────────────────────────────────
    public Task<IReadOnlyList<Fingerprint>> ListFingerprintsAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Fingerprint>>(Array.Empty<Fingerprint>());

    public Task<Fingerprint?> GetFingerprintAsync(string fingerprintId, CancellationToken ct = default)
        => Task.FromResult<Fingerprint?>(null);

    public Task<string?> LookupFingerprintIdAsync(string primarySignature, CancellationToken ct = default)
        => Task.FromResult<string?>(null);

    public Task<IReadOnlyDictionary<string, int>> GetUnabsorbedObservationCountsAsync(CancellationToken ct = default)
        => Task.FromResult(_emptyCounts);

    public Task<int> GetUnabsorbedObservationCountAsync(string fingerprintId, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task<IReadOnlyList<NearestFingerprint>> GetNearestForSignatureAsync(
        string primarySignature, int k, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<NearestFingerprint>>(Array.Empty<NearestFingerprint>());

    public Task<IReadOnlyList<RootHistoryEntry>> GetRootHistoryAsync(
        string fingerprintId, int limit = 20, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<RootHistoryEntry>>(Array.Empty<RootHistoryEntry>());

    // ── Verdict cache ────────────────────────────────────────────────────────
    public Task UpdateCachedVerdictAsync(
        string fingerprintId, double botProbability, string? riskBand, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RecordVerdictAsync(
        string fingerprintId, double botProbability, string? riskBand, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task BumpCachedScoreCheckedAtAsync(string fingerprintId, CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Matcher write path ───────────────────────────────────────────────────
    public Task InsertFingerprintAsync(Fingerprint fp, string primarySignature, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpsertKeyAsync(string primarySignature, string fingerprintId, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RecordObservationAsync(string fingerprintId, float[] vector, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task RecordCorrectionAsync(
        string requestId, string primarySignature, string? pass1FingerprintId, string pass2FingerprintId,
        float[] differentiator, float[] updatedPass2Weights, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task AbsorbObservationAsync(
        long observationId, string fingerprintId, float[] newCentroid, int newMaturity, float[] newWeights,
        string? newInferredClientType = null, double newInferredTypeConfidence = 0,
        bool inferredTypeChanged = false, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task UpdateRollupCentroidAsync(
        string fingerprintId, float[] newCentroid, int newMaturity, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<double> BumpAmbiguityPersistenceAsync(
        string fingerprintId, bool isAmbiguityEvent, double alpha, CancellationToken ct = default)
        => Task.FromResult(0.0);

    // ── Display name ─────────────────────────────────────────────────────────
    public Task UpdateDisplayNameAsync(
        string fingerprintId, string displayName, DateTime updatedAt, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<int> CountByDisplayNameAsync(string displayName, CancellationToken ct = default)
        => Task.FromResult(0);

    public Task UpdateDisplayNameForSignatureAsync(
        string primarySignature, string displayName, DateTime updatedAt, CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Batch read / drift / absorption picker ───────────────────────────────
    public Task<IReadOnlyDictionary<string, float[]>> GetCentroidsBySignaturesAsync(
        IReadOnlyCollection<string> primarySignatures, CancellationToken ct = default)
        => Task.FromResult(_emptyCentroids);

    public Task<IReadOnlyList<(string FingerprintId, float[] Vector)>> ListActiveObservationsAsync(
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string FingerprintId, float[] Vector)>>(
            Array.Empty<(string, float[])>());

    public Task<IReadOnlyList<Fingerprint>> ListStaleScoreFingerprintsAsync(
        int ttlSeconds, int batchSize, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<Fingerprint>>(Array.Empty<Fingerprint>());

    public Task<float[]?> GetLatestObservationVectorAsync(string fingerprintId, CancellationToken ct = default)
        => Task.FromResult<float[]?>(null);

    public Task<IReadOnlyList<AbsorbableObservation>> ListAbsorbableObservationsAsync(
        int maturityThreshold, int ageDays, int activeWindowDays, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<AbsorbableObservation>>(Array.Empty<AbsorbableObservation>());

    // ── Calibration ──────────────────────────────────────────────────────────
    public Task UpsertGlobalWeightsAsync(
        float[] weights, int samplesUsed, int clustersUsed, int archetypesUsed, CancellationToken ct = default)
        => Task.CompletedTask;

    public Task<(float[] Weights, DateTime LastComputedAt)?> GetGlobalWeightsAsync(CancellationToken ct = default)
        => Task.FromResult<(float[], DateTime)?>(null);

    public Task UpsertArchetypeAsync(IdentityArchetype archetype, CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Vec KNN ──────────────────────────────────────────────────────────────
    public Task<IReadOnlyList<(string FingerprintId, double Distance)>> SearchVecCentroidsAsync(
        float[] queryVector, int k, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string FingerprintId, double Distance)>>(
            Array.Empty<(string, double)>());

    public Task<IReadOnlyList<(string FingerprintId, double Distance)>> SearchVecObservationsAsync(
        float[] queryVector, int k, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<(string FingerprintId, double Distance)>>(
            Array.Empty<(string, double)>());

    // ── Cluster-driven root reseat ───────────────────────────────────────────
    public Task ReseatRootCentroidsAsync(
        IReadOnlyCollection<ClusterRootUpdate> updates,
        int minMemberFingerprints = 2,
        CancellationToken ct = default)
        => Task.CompletedTask;

    // ── Test rig ─────────────────────────────────────────────────────────────
    public Task<IReadOnlyDictionary<string, int>> TruncateAllAsync(CancellationToken ct = default)
        => Task.FromResult(_emptyCounts);
}
