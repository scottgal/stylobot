using System.Globalization;
using Mostlylucid.BotDetection.UI.Models;

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
    ///     Drill anchor for the tile footer. Lives on the same dashboard
    ///     middleware (B2 wired this route).
    /// </summary>
    public const string DrillPath = "/dashboard/aspnet-hub";

    private readonly IAspNetPackHubBuilder? _builder;

    public AspNetPackHealthSummaryProvider(IAspNetPackHubBuilder? builder = null)
    {
        _builder = builder;
    }

    /// <inheritdoc />
    public int Order => 200;

    /// <inheritdoc />
    public async Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
    {
        if (_builder is null) return null;

        var summary = await _builder.BuildSummaryAsync(ct).ConfigureAwait(false);

        var value = summary.EndpointCount is null
            ? "-"
            : summary.EndpointCount.Value.ToString("N0", CultureInfo.InvariantCulture);

        return new StatTileViewModel(
            Title: "ASP.NET Pack",
            Value: value,
            Unit: "endpoints",
            HealthBand: summary.HealthBand,
            DrillHref: DrillPath,
            DrillLabel: "View pack");
    }
}
