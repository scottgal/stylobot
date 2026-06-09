using Microsoft.Extensions.Logging;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services;

/// <summary>
///     Composes the dashboard overview pack-health row (C1 of the Pack
///     Metrics + Cohesive Views plan). Enumerates every registered
///     <see cref="IPackHealthSummaryProvider"/>, orders them by their
///     <see cref="IPackHealthSummaryProvider.Order"/>, and returns the
///     non-null tiles in render order.
///     <para>
///         Resilience: a single provider returning null is normal (its
///         backing surface isn't enabled on this host). A provider that
///         THROWS does not take down the row; the exception is logged and
///         the row continues with the remaining tiles. The one exception is
///         <see cref="OperationCanceledException"/>, which propagates so the
///         request can shut down cleanly.
///     </para>
/// </summary>
public sealed class DashboardOverviewPackHealthRowBuilder
{
    private readonly IEnumerable<IPackHealthSummaryProvider> _providers;
    private readonly ILogger<DashboardOverviewPackHealthRowBuilder>? _logger;

    public DashboardOverviewPackHealthRowBuilder(
        IEnumerable<IPackHealthSummaryProvider> providers,
        ILogger<DashboardOverviewPackHealthRowBuilder>? logger = null)
    {
        _providers = providers ?? Array.Empty<IPackHealthSummaryProvider>();
        _logger = logger;
    }

    /// <summary>
    ///     Resolve every tile. Providers are visited in ascending
    ///     <see cref="IPackHealthSummaryProvider.Order"/>; ties are stable in
    ///     the registration order returned by DI.
    /// </summary>
    public async Task<DashboardOverviewPackHealthRowViewModel> BuildAsync(CancellationToken ct)
    {
        var ordered = _providers.OrderBy(p => p.Order).ToList();
        var tiles = new List<StatTileViewModel>(ordered.Count);
        foreach (var provider in ordered)
        {
            try
            {
                var tile = await provider.BuildTileAsync(ct).ConfigureAwait(false);
                if (tile is not null) tiles.Add(tile);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A misbehaving provider does NOT take down the row -- the
                // operator should still see the other packs. Logged here so
                // ops can spot a provider that's silently failing.
                _logger?.LogWarning(
                    ex,
                    "Pack-health provider {Provider} threw while building its tile; skipping.",
                    provider.GetType().Name);
            }
        }
        return new DashboardOverviewPackHealthRowViewModel(tiles);
    }
}

/// <summary>
///     View model bound to the <c>SbPackHealthRow</c> view component / partial.
///     A list of pre-composed <see cref="StatTileViewModel"/>s in render order.
///     Empty list = row renders nothing (the existing overview widgets below
///     take over for the truly-empty install case).
/// </summary>
public sealed record DashboardOverviewPackHealthRowViewModel(IReadOnlyList<StatTileViewModel> Tiles);
