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

    /// <summary>Reads an already-materialized bundle without composing; false on miss.</summary>
    bool TryGet(
        DashboardPageManifest manifest, DashboardPageWindow window, long tick, out DashboardPageResult? result);
}
