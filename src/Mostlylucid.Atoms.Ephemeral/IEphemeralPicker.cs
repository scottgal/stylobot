namespace Mostlylucid.Atoms.Ephemeral;

/// <summary>
///     Caller-owned LFU walk that surfaces items needing LLM attention this tick.
///     Pure in-memory read against the caller's atom; MUST NOT open a DB
///     connection or do any I/O. Cold items are skipped naturally — the picker
///     returns only what is currently hot AND needs work.
/// </summary>
public interface IEphemeralPicker<out TItem>
{
    /// <summary>
    ///     Returns at most <paramref name="maxCount"/> hot items. Empty result is
    ///     normal (nothing needs LLM attention this tick) and produces no log
    ///     spam in the coordinator.
    /// </summary>
    IReadOnlyList<TItem> Pick(int maxCount);
}
