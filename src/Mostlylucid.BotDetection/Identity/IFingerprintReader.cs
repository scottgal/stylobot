namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Read-only slice of the fingerprint store consumed by the dashboard (Identities tab,
///     per-fingerprint drill-in, REST endpoints). Split out so a remote-mode dashboard
///     host can satisfy fingerprint reads over HTTP without dragging in
///     <see cref="SqliteFingerprintStore"/>'s write surface (centroid updates,
///     observation absorption, score caching, etc.).
///
///     The set of methods here mirrors exactly what the dashboard middleware and view
///     components call - it is intentionally minimal. Anything write-side or detection-
///     pipeline-only stays on the concrete <see cref="SqliteFingerprintStore"/>.
/// </summary>
public interface IFingerprintReader
{
    /// <summary>List all fingerprints (most-recent first). Used by the Identities tab.</summary>
    Task<IReadOnlyList<Fingerprint>> ListFingerprintsAsync(CancellationToken ct = default);

    /// <summary>Fetch a single fingerprint by id. Returns null when unknown.</summary>
    Task<Fingerprint?> GetFingerprintAsync(string fingerprintId, CancellationToken ct = default);

    /// <summary>
    ///     L1 lookup: resolve the fingerprint id currently bound to
    ///     <paramref name="primarySignature"/> in <c>fingerprint_keys</c>, or null when
    ///     no binding exists yet. Used by dashboard render paths to recover the
    ///     fingerprint id on upstream-trust / verdict-cache fast-paths that bypass
    ///     the orchestrator (so HttpContext.Items never carries it).
    /// </summary>
    Task<string?> LookupFingerprintIdAsync(string primarySignature, CancellationToken ct = default);

    /// <summary>
    ///     Per-fingerprint unabsorbed observation counts (drift candidates). Used to sort
    ///     the Identities tab so visitors with fresh data waiting float to the top.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetUnabsorbedObservationCountsAsync(CancellationToken ct = default);

    /// <summary>Focused unabsorbed-observation count for one fingerprint id.</summary>
    Task<int> GetUnabsorbedObservationCountAsync(string fingerprintId, CancellationToken ct = default);

    /// <summary>
    ///     Resolve the fingerprint bound to <paramref name="primarySignature"/> and run a
    ///     vec0 KNN against <c>fingerprints_vec</c>, returning up to <paramref name="k"/>
    ///     neighbours excluding self, ordered by ascending L2 distance. This is a
    ///     view-time calculation — the answer drifts as the population of centroids
    ///     evolves, so it must never be cached on the fingerprint row.
    ///
    ///     <para>
    ///     Returns an empty list when identity is disabled, the signature is unbound,
    ///     vec0 is not available, or no other centroids exist. Implementations that
    ///     cannot satisfy the lookup (e.g. remote-mode readers without a matching
    ///     endpoint) also return empty rather than throwing.
    ///     </para>
    /// </summary>
    Task<IReadOnlyList<NearestFingerprint>> GetNearestForSignatureAsync(
        string primarySignature, int k, CancellationToken ct = default);
}

/// <summary>
///     One neighbour returned by <see cref="IFingerprintReader.GetNearestForSignatureAsync"/>.
///     <see cref="Distance"/> is the raw L2 distance from <c>fingerprints_vec</c>; lower means
///     more similar. <see cref="DisplayName"/> is the neighbour's persisted name (empty for
///     legacy rows that haven't been re-matched since the column was added).
/// </summary>
public sealed record NearestFingerprint(
    string FingerprintId,
    string DisplayName,
    string InferredClientType,
    double Distance);
