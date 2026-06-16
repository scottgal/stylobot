using System.Globalization;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

/// <summary>
///     Pack-health row tile for the ASP.NET pack. Wraps
///     <see cref="IAspNetPackHubBuilder.BuildSummaryAsync"/> and shapes the
///     answer into an <see cref="StatTileViewModel"/> sitting next to the
///     policy stack + meters tiles on the dashboard overview.
///     <para>
///         The pack is OPTIONAL -- on viewer-mode hosts that pull data from a
///         remote gateway, no <c>IAspNetPackHubBuilder</c> is registered. The
///         provider returns <c>null</c> in that case per
///         <c>feedback_remote_mode_optional_di</c>, and the row builder skips
///         this tile silently.
///     </para>
/// </summary>
public sealed class AspNetPackHealthSummaryProvider : IPackHealthSummaryProvider
{
    /// <summary>
    ///     Drill subpath for the tile footer; resolved through
    ///     <see cref="IDashboardLinkResolver" /> at render time so the tile
    ///     follows the dashboard's configured BasePath rather than
    ///     hard-coding the default <c>/dashboard</c> prefix.
    /// </summary>
    public const string DrillSubPath = "/aspnet-hub";

    private readonly IAspNetPackHubBuilder? _builder;
    private readonly AspNetPackHubTileCache? _cache;
    private readonly IDashboardLinkResolver? _links;

    public AspNetPackHealthSummaryProvider(
        IAspNetPackHubBuilder? builder = null,
        AspNetPackHubTileCache? cache = null,
        IDashboardLinkResolver? links = null)
    {
        _builder = builder;
        _cache = cache;
        _links = links;
    }

    /// <inheritdoc />
    public int Order => 200;

    /// <inheritdoc />
    public async Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
    {
        if (_builder is null) return null;

        // Centralised cache + beacon path. The commercial AspNetPack
        // bridge invalidates this cache when the underlying inventory /
        // meter catalog changes. Per feedback_centralised_change_detection.
        var cached = _cache?.TryGet();
        if (cached is not null) return cached;

        var summary = await _builder.BuildSummaryAsync(ct).ConfigureAwait(false);

        var value = summary.EndpointCount is null
            ? "-"
            : summary.EndpointCount.Value.ToString("N0", CultureInfo.InvariantCulture);

        var drill = _links?.Resolve(DrillSubPath)
                    ?? "/stylobot" + DrillSubPath;

        var tile = new StatTileViewModel(
            Title: "ASP.NET Pack",
            Value: value,
            Unit: "endpoints",
            HealthBand: summary.HealthBand,
            DrillHref: drill,
            DrillLabel: "View pack",
            BeaconKey: DashboardFreshnessBeacon.Surfaces.AspNetPackHub);

        _cache?.Set(tile);
        return tile;
    }
}
