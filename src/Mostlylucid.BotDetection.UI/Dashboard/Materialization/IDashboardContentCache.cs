using Mostlylucid.BotDetection.UI.Dashboard.Composition;

namespace Mostlylucid.BotDetection.UI.Dashboard.Materialization;

/// <summary>
///     Gateway-owned content cache for materialized dashboard bundles, keyed by
///     (envelope, tick). Fresh-by-tick — NOT a stale data cache; the tick
///     materializer refreshes hot pages out-of-band and the request path reads the
///     result. Backed by the canonical <c>SlidingCacheAtom</c>.
/// </summary>
public interface IDashboardContentCache
{
    /// <summary>
    ///     Returns the materialized bundle for (manifest, window) at <paramref name="tick"/>,
    ///     composing once on a cold miss (the atom's factory). Shared by the request-path
    ///     read and the materializer's warm — whoever arrives first composes, the other reads.
    /// </summary>
    Task<DashboardPageResult> GetAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct);

    /// <summary>
    ///     Convenience read at the cache's current tick — the request-path entrypoint,
    ///     so callers don't need the change cursor. Equivalent to <see cref="GetAsync"/>
    ///     with the current tick.
    /// </summary>
    Task<DashboardPageResult> GetCurrentAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, CancellationToken ct);

    /// <summary>
    ///     Warms (materializes) the bundle for (manifest, window) at <paramref name="tick"/>
    ///     WITHOUT recording the envelope as live. The tick materializer uses this so its
    ///     own warm reads don't keep an envelope alive — only genuine user reads
    ///     (<see cref="GetAsync"/>) refresh liveness, so unviewed envelopes age out.
    /// </summary>
    Task<DashboardPageResult> WarmAsync(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, CancellationToken ct);

    /// <summary>Reads an already-materialized bundle without composing; false on miss.</summary>
    bool TryGet(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, out DashboardPageResult? result);

    /// <summary>
    ///     The (manifest, window) pairs read recently enough to still be "live" — the
    ///     tick materializer re-warms exactly these at the current tick so reads hit
    ///     instead of composing. Prunes envelopes older than the configured max age.
    ///     This is the demand signal until SignalR presence lands: only viewed
    ///     envelopes are warmed, and they age out when no longer read.
    /// </summary>
    IReadOnlyCollection<(DashboardPageManifest Manifest, DashboardPageWindow Window)> LiveEnvelopes();
}
