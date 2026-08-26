using System.Globalization;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;
using Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

namespace Mostlylucid.BotDetection.PrometheusPack.HealthSummaryProviders;

/// <summary>
///     Pack-health row tile for the gateway's published meter catalog.
///     Wraps <see cref="IMeterStream.ListAsync"/> for a count of currently
///     registered instruments.
///     <para>
///         Lives in the Prometheus pack (not the UI assembly) so the pack
///         OWNS its dashboard surface: <c>AddPrometheusPack</c> registers this
///         provider against the UI's <see cref="IPackHealthSummaryProvider"/>
///         seam. A host that never installs the pack simply has no meter tile --
///         no hard dependency flows out of the UI package.
///     </para>
///     <para>
///         <see cref="IMeterStream"/> is optional per
///         <c>feedback_remote_mode_optional_di</c>: returning <c>null</c> from
///         <see cref="BuildTileAsync"/> when the dependency is absent mirrors
///         the fallback the other pack-health providers use; the row builder
///         simply omits the tile.
///     </para>
/// </summary>
public sealed class MeterStreamHealthSummaryProvider : IPackHealthSummaryProvider
{
    /// <summary>
    ///     Drill subpath for the tile footer; resolved through
    ///     <see cref="IDashboardLinkResolver" /> at render time so the tile
    ///     follows the dashboard's configured BasePath rather than
    ///     hard-coding the default <c>/dashboard</c> prefix.
    /// </summary>
    public const string DrillSubPath = "/insights";

    private readonly IMeterStream? _stream;
    private readonly MeterStreamHealthTileCache? _cache;
    private readonly IDashboardLinkResolver? _links;

    public MeterStreamHealthSummaryProvider(
        IMeterStream? stream = null,
        MeterStreamHealthTileCache? cache = null,
        IDashboardLinkResolver? links = null)
    {
        _stream = stream;
        _cache = cache;
        _links = links;
    }

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public async Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
    {
        if (_stream is null) return null;

        // Centralised cache + beacon path. The pack's MeterHealthFreshnessBootstrap
        // ticks on Tick10s and invalidates this cache when the catalog size
        // changes; subsequent BuildTileAsync rebuilds via ListAsync.
        // Per feedback_centralised_change_detection.
        var cached = _cache?.TryGet();
        if (cached is not null) return cached;

        var catalog = await _stream.ListAsync(ct).ConfigureAwait(false);
        var count = catalog.Count;

        var drill = _links?.Resolve(DrillSubPath)
                    ?? "/stylobot" + DrillSubPath;

        var tile = new StatTileViewModel(
            Title: "Metrics",
            Value: count.ToString("N0", CultureInfo.InvariantCulture),
            Unit: "meters",
            HealthBand: count > 0 ? HealthBand.Good : HealthBand.Caution,
            DrillHref: drill,
            DrillLabel: "Open insights",
            BeaconKey: DashboardFreshnessBeacon.Surfaces.MeterStreamHealth);

        _cache?.Set(tile);
        return tile;
    }
}
