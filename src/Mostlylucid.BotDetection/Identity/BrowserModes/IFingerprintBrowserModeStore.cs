namespace Mostlylucid.BotDetection.Identity.BrowserModes;

/// <summary>
///     Per-fingerprint browser-mode storage surface. Mirrors the shape of
///     <see cref="IFingerprintStore"/> for the new <c>fingerprint_modes</c>
///     table — one row per (fingerprint_id, mode_id) tuple.
///
///     Reads are LFU-cached the same way <see cref="IFingerprintStore"/>'s
///     reads are; writes invalidate the per-fingerprint cache slot and
///     write through to the underlying store. The signal-driven persistence
///     atom (build step 3+) sits on top of these primitives.
///
///     See docs/architecture/composite-browser-mode-fingerprints.md.
/// </summary>
public interface IFingerprintBrowserModeStore
{
    /// <summary>
    ///     Returns every browser-mode row for <paramref name="fingerprintId"/>,
    ///     newest-last. Empty list when the fingerprint has no mode rows
    ///     (e.g. it was allocated before the schema landed and the seed
    ///     migration has not yet folded it in).
    /// </summary>
    Task<IReadOnlyList<FingerprintBrowserMode>> GetModesAsync(
        string fingerprintId, CancellationToken ct = default);

    /// <summary>
    ///     Fetch one mode row by composite key. Returns null when the parent
    ///     fingerprint has not yet shown this mode.
    /// </summary>
    Task<FingerprintBrowserMode?> GetModeAsync(
        string fingerprintId, string modeId, CancellationToken ct = default);

    /// <summary>
    ///     Upsert the mode row. Insert when the (fingerprint_id, mode_id)
    ///     tuple is new, update centroid/weights/maturity/observation_count/
    ///     last_seen otherwise. first_seen is preserved on update.
    /// </summary>
    Task UpsertModeAsync(FingerprintBrowserMode mode, CancellationToken ct = default);

    /// <summary>
    ///     Delete one mode row by composite key. Used by the prune atom that
    ///     drops sparse modes once observation_count and last_seen pass their
    ///     configurable floors.
    /// </summary>
    Task DeleteModeAsync(string fingerprintId, string modeId, CancellationToken ct = default);

    /// <summary>
    ///     Append-only insert of a per-mode observation row. The matcher calls
    ///     this on the request path — no read, no merge — so the hot path is a
    ///     single SQL insert with no race. The drainer (FingerprintMode
    ///     AbsorptionService) batches absorption per (fingerprint_id, mode_id)
    ///     tuple on a tick.
    /// </summary>
    Task RecordModeObservationAsync(
        string fingerprintId, string modeId, float[] vector,
        string? uaFamily = null,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="maxRows"/> unabsorbed mode observation
    ///     rows grouped per (fingerprint_id, mode_id) tuple. The drainer walks
    ///     this batch, EWMA-merges the vectors into the cached mode centroid,
    ///     and calls <see cref="AbsorbModeObservationsAsync"/> to commit the
    ///     updated mode row and mark the observation rows absorbed.
    /// </summary>
    Task<IReadOnlyList<UnabsorbedModeObservation>> ListUnabsorbedModeObservationsAsync(
        int maxRows, CancellationToken ct = default);

    /// <summary>
    ///     Commit a batched absorption: write the new mode row state (centroid /
    ///     maturity / observation_count / last_seen) and mark the listed
    ///     observation ids absorbed in one transaction. The LFU cache slot for
    ///     the fingerprint is invalidated so the next read sees the new state.
    /// </summary>
    Task AbsorbModeObservationsAsync(
        FingerprintBrowserMode updated,
        IReadOnlyList<long> observationIds,
        CancellationToken ct = default);

    /// <summary>
    ///     Returns up to <paramref name="maxRows"/> fingerprint ids that have
    ///     at least one mode row, oldest <c>last_seen</c> first. The rollup
    ///     recompute sweep walks this list to find fingerprints whose parent
    ///     centroid is stale relative to their child mode evolution.
    /// </summary>
    Task<IReadOnlyList<string>> ListFingerprintIdsWithModesAsync(
        int maxRows, CancellationToken ct = default);

}

/// <summary>
///     One unabsorbed observation row returned by the drainer's batch fetch.
/// </summary>
public sealed record UnabsorbedModeObservation(
    long ObservationId,
    string FingerprintId,
    string ModeId,
    float[] Vector,
    DateTime ObservedAt,
    string? UaFamily = null);
