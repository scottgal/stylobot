namespace Mostlylucid.BotDetection.Identity;

/// <summary>
///     Dashboard live-update beacon for fingerprint name-slot edits (HR2). Implementations
///     deliver the beacon to every connected browser; the browser then HTMX-swaps the
///     <c>.fp-name[data-fp-id]</c> wrapper for the affected fingerprint from the commercial
///     <c>/api/v1/commercial/fingerprints/{id}/given-name/read</c> endpoint so the row
///     repaints without an F5.
///     <para>
///         The abstraction lives in the FOSS identity namespace -- alongside
///         <see cref="IFingerprintStore"/> -- so the commercial editor + Redis subscriber can
///         resolve it without taking a project reference on
///         <c>Mostlylucid.BotDetection.UI</c>. The SignalR-backed implementation lives
///         FOSS-side in the dashboard package and is wired automatically by
///         <c>AddStyloBotDashboard</c>; hosts without the dashboard get a no-op default so
///         the commercial code can fire-and-forget without a null guard.
///     </para>
/// </summary>
public interface IFingerprintDirtyBroadcaster
{
    /// <summary>
    ///     Notify every connected dashboard browser that the named slot on
    ///     <paramref name="fingerprintId"/> changed. Implementations must be fire-and-forget
    ///     safe -- never throw, never block longer than the underlying transport's own
    ///     enqueue cost -- because the call sites are inside operator-edit hot paths and a
    ///     transport failure must not turn into a 500 on the editor POST.
    /// </summary>
    /// <param name="fingerprintId">Fingerprint whose name slot moved.</param>
    /// <param name="slot">
    ///     Slot kind: <c>given</c> (operator edit), <c>llm</c> (LLM coordinator writeback),
    ///     or <c>induced</c> (matcher writeback). Unknown slots are forwarded as-is so
    ///     future slots don't need a coordinated client update.
    /// </param>
    /// <param name="ct">Cancellation token for the dispatch.</param>
    Task PublishAsync(string fingerprintId, string slot, CancellationToken ct = default);
}

/// <summary>
///     No-op default registered by FOSS DI. Hosts without
///     <c>AddStyloBotDashboard</c> (lightweight viewer hosts, test fixtures, the
///     bare gateway container without the bundled dashboard) get this so commercial
///     code can resolve the broadcaster unconditionally and drop the beacon on the
///     floor when no UI is wired up.
/// </summary>
public sealed class NoOpFingerprintDirtyBroadcaster : IFingerprintDirtyBroadcaster
{
    /// <inheritdoc />
    public Task PublishAsync(string fingerprintId, string slot, CancellationToken ct = default)
        => Task.CompletedTask;
}
