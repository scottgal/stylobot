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
    ///     last_seen/inferred_archetype/inferred_confidence otherwise.
    ///     first_seen is preserved on update.
    /// </summary>
    Task UpsertModeAsync(FingerprintBrowserMode mode, CancellationToken ct = default);

    /// <summary>
    ///     Delete one mode row by composite key. Used by the prune atom that
    ///     drops sparse modes once observation_count and last_seen pass their
    ///     configurable floors.
    /// </summary>
    Task DeleteModeAsync(string fingerprintId, string modeId, CancellationToken ct = default);
}
