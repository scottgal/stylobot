namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     One row of fingerprint name change history. Snapshot record -- written by
///     <see cref="IFingerprintStore.UpdateDisplayNameAsync"/> whenever a Fingerprint's
///     <c>DisplayName</c> transitions to a new non-empty value. Read by the signature
///     timeline view on demand (NOT cached in the LFU dicts -- snapshots are cold
///     storage).
/// </summary>
/// <param name="OldName">Name before the change. Null when this is the first name assigned.</param>
/// <param name="NewName">Name after the change. Always non-empty (empty-to-empty transitions are not recorded).</param>
/// <param name="Source">
///     What set the name: <c>"matcher"</c> (FingerprintMatchContributor recompose),
///     <c>"llm"</c> (deterministic / LLM rename callback), or <c>"operator"</c>
///     (future explicit rename via dashboard control).
/// </param>
/// <param name="ChangedAt">UTC timestamp of the transition.</param>
/// <param name="SignalSnapshotJson">
///     Projection-input snapshot at the time this name was generated -- the
///     fingerprint's directly-observed signals (UA family/version, OS, modifiers,
///     observation/member counts, claim status). Null on pre-2026-06-26 rows; the
///     N3 schema migration adds the column nullable so legacy rows stay readable.
///     See docs/superpowers/specs/2026-06-26-fingerprint-name-projection-restore.md.
/// </param>
public sealed record DisplayNameChange(
    string? OldName,
    string NewName,
    string Source,
    DateTime ChangedAt,
    string? SignalSnapshotJson = null);
