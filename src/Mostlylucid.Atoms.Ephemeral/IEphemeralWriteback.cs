namespace Mostlylucid.Atoms.Ephemeral;

/// <summary>
///     Caller-owned persistence of a successful invocation. Frequency-appropriate:
///     write-behind for matcher-rate items, synchronous + transactional for
///     human-rate items — entirely the caller's choice. The coordinator just
///     awaits the returned task.
/// </summary>
public interface IEphemeralWriteback<in TItem, in TResult>
{
    Task ApplyAsync(TItem item, TResult result, CancellationToken ct);
}
