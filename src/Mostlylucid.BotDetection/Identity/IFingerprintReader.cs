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
    ///     Per-fingerprint unabsorbed observation counts (drift candidates). Used to sort
    ///     the Identities tab so visitors with fresh data waiting float to the top.
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetUnabsorbedObservationCountsAsync(CancellationToken ct = default);

    /// <summary>Focused unabsorbed-observation count for one fingerprint id.</summary>
    Task<int> GetUnabsorbedObservationCountAsync(string fingerprintId, CancellationToken ct = default);
}
