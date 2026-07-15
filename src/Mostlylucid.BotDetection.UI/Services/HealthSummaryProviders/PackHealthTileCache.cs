using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

/// <summary>
///     Tiny in-memory cache for a single composed <see cref="StatTileViewModel"/>.
///     Used by the pack-health providers (meter-stream catalog + ASP.NET pack
///     hub) so they don't repeat their underlying compute on every dashboard
///     refresh. The cache is invalidated by the centralised
///     <see cref="DashboardFreshnessBeacon"/> path, not by a private TTL --
///     same shape as <see cref="PolicyStackSummaryCache"/> but parameterised
///     on the tile model.
///
///     <para>
///         One instance per surface (DI registers a named singleton for each
///         provider that opts into caching). The cache lives in the UI
///         project so producers in PrometheusPack / AspNetPack can invalidate
///         it without taking a back-reference to those packs.
///     </para>
///
///     <para>
///         Acceptable as in-memory state per
///         <c>feedback_no_inmemory_stores</c>: this is a runtime cache of a
///         computation; the underlying data lives in the meter stream /
///         endpoint inventory; the cache is rebuilt on miss.
///     </para>
/// </summary>
public class PackHealthTileCache
{
    private readonly object _gate = new();
    private StatTileViewModel? _cached;

    /// <summary>
    ///     Return the cached tile or <c>null</c> when the cache has never
    ///     been populated or was invalidated. The caller rebuilds on miss
    ///     and stores the result via <see cref="Set"/>.
    /// </summary>
    public StatTileViewModel? TryGet()
    {
        lock (_gate) return _cached;
    }

    /// <summary>Store a freshly-built tile in the cache.</summary>
    public void Set(StatTileViewModel tile)
    {
        ArgumentNullException.ThrowIfNull(tile);
        lock (_gate) _cached = tile;
    }

    /// <summary>
    ///     Drop the cached tile. Producers invoke this from the freshness
    ///     bridge when the underlying inventory / catalog snapshot
    ///     changes; the next read rebuilds.
    /// </summary>
    public void Invalidate()
    {
        lock (_gate) _cached = null;
    }
}

/// <summary>
///     DI marker: the cache instance for the meter-stream catalog tile.
///     Separated as a subclass so the DI container can resolve a distinct
///     singleton per surface without keyed-services machinery.
/// </summary>
public sealed class MeterStreamHealthTileCache : PackHealthTileCache
{
}
