using System.Globalization;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

/// <summary>
///     Pack-health row tile for the gateway's published meter catalog.
///     Wraps <see cref="IMeterStream.ListAsync"/> for a count of currently
///     registered instruments.
///     <para>
///         Always returns a tile -- <see cref="IMeterStream"/> is registered
///         on every host (Local on the gateway, Remote on viewer hosts), so
///         this provider doesn't need the optional-DI fallback the other two
///         use. An empty catalog renders Caution; any entries render Good.
///     </para>
/// </summary>
public sealed class MeterStreamHealthSummaryProvider : IPackHealthSummaryProvider
{
    /// <summary>
    ///     Drill anchor for the tile footer. Targets the global insights page
    ///     where the operator can browse the full meter catalog.
    /// </summary>
    public const string DrillPath = "/dashboard/insights";

    private readonly IMeterStream _stream;

    public MeterStreamHealthSummaryProvider(IMeterStream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
    }

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public async Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
    {
        var catalog = await _stream.ListAsync(ct).ConfigureAwait(false);
        var count = catalog.Count;

        return new StatTileViewModel(
            Title: "Metrics",
            Value: count.ToString("N0", CultureInfo.InvariantCulture),
            Unit: "meters",
            HealthBand: count > 0 ? HealthBand.Good : HealthBand.Caution,
            DrillHref: DrillPath,
            DrillLabel: "Open insights");
    }
}
