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
    /// <summary>
    ///     List ALL fingerprints (most-recent first), unbounded. Reserved for internal batch
    ///     work that genuinely needs the whole population in one pass (brute-force anchor
    ///     index rebuild, weight calibration ticks) — <b>never call this from a request-serving
    ///     path</b> (API endpoint, dashboard page render). Those must use
    ///     <see cref="ListFingerprintsAsync(int,int,CancellationToken)"/> instead, which caps
    ///     what a single call can return. conn- 2026-08-08: this method's lack of a bound was
    ///     exactly what let <c>GET /api/v1/fingerprints</c> materialise an entire table (every
    ///     row carrying a full centroid vector) on every call — see the bounded overload below.
    /// </summary>
    Task<IReadOnlyList<Fingerprint>> ListFingerprintsAsync(CancellationToken ct = default);

    /// <summary>
    ///     List fingerprints (most-recent first) bounded to a page — the version request-serving
    ///     paths must use, never <see cref="ListFingerprintsAsync(CancellationToken)"/>.
    ///     <paramref name="limit"/> is clamped to a sane maximum by the implementation
    ///     regardless of what the caller requests, so a caller cannot get "everything" by
    ///     passing a huge limit. Default implementation is a correctness-preserving fallback
    ///     (fetches the unbounded list, then pages in memory) for implementers that have not
    ///     been given a native paged query yet — <see cref="SqliteFingerprintStore"/> and the
    ///     Postgres commercial store override this with real LIMIT/OFFSET SQL.
    /// </summary>
    async Task<IReadOnlyList<Fingerprint>> ListFingerprintsAsync(int offset, int limit, CancellationToken ct = default)
    {
        var clamped = Math.Clamp(limit, 1, DefaultMaxPageSize);
        var all = await ListFingerprintsAsync(ct).ConfigureAwait(false);
        return all.Skip(Math.Max(offset, 0)).Take(clamped).ToList();
    }

    /// <summary>
    ///     Fallback page-size ceiling for <see cref="ListFingerprintsAsync(int,int,CancellationToken)"/>
    ///     implementers that don't expose their own configurable cap. The two real stores
    ///     (SQLite, Postgres) have their own Options-bound cap and don't use this constant.
    /// </summary>
    protected const int DefaultMaxPageSize = 200;

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

    /// <summary>
    ///     Timeline of the fingerprint's root_centroid evolution: archetype seed,
    ///     each cluster snapshot that reseated the root, in newest-first order. The
    ///     dashboard renders this so operators can see how the reference shape
    ///     shifted as the population's centroids refined themselves -- the
    ///     visible record of the adaptation loop.
    /// </summary>
    Task<IReadOnlyList<RootHistoryEntry>> GetRootHistoryAsync(
        string fingerprintId, int limit = 20, CancellationToken ct = default);
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

/// <summary>
///     One row of the fingerprint's <c>fingerprint_root_history</c> table.
///     <see cref="RootCentroid"/> is the snapshot vector that drift was measured
///     against during the row's active window (between <see cref="SetAt"/> and
///     <see cref="SupersededAt"/>; null on the latter means this row is the
///     currently active root). <see cref="RootSource"/> is the lineage marker
///     (<c>archetype:&lt;id&gt;</c> / <c>cluster:&lt;id&gt;</c> /
///     <c>verifiedbot:&lt;name&gt;</c> / <c>bootstrap</c>).
/// </summary>
public sealed record RootHistoryEntry(
    long Id,
    string FingerprintId,
    float[] RootCentroid,
    string RootSource,
    int MemberCount,
    DateTime SetAt,
    DateTime? SupersededAt);
