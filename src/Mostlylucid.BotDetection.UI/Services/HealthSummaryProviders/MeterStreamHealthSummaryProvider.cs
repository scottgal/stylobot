using System.Globalization;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.UI.Services.HealthSummaryProviders;

/// <summary>
///     Pack-health row tile for the gateway's published meter catalog.
///     Wraps <see cref="IMeterStream.ListAsync"/> for a count of currently
///     registered instruments.
///     <para>
///         <see cref="IMeterStream"/> is optional: a Demo / dev-bench host
///         that doesn't call <c>AddPrometheusPack</c> won't have one, and
///         the dashboard wiring registers this provider unconditionally.
///         Returning <c>null</c> from <see cref="BuildTileAsync"/> when the
///         dependency is absent mirrors the fallback the other two
///         pack-health providers use; the row builder simply omits the tile.
///     </para>
/// </summary>
public sealed class MeterStreamHealthSummaryProvider : IPackHealthSummaryProvider
{
    /// <summary>
    ///     Drill anchor for the tile footer. Targets the global insights page
    ///     where the operator can browse the full meter catalog.
    /// </summary>
    public const string DrillPath = "/dashboard/insights";

    private readonly IMeterStream? _stream;
    private readonly MeterStreamHealthTileCache? _cache;

    public MeterStreamHealthSummaryProvider(
        IMeterStream? stream = null,
        MeterStreamHealthTileCache? cache = null)
    {
        _stream = stream;
        _cache = cache;
    }

    /// <inheritdoc />
    public int Order => 300;

    /// <inheritdoc />
    public async Task<StatTileViewModel?> BuildTileAsync(CancellationToken ct)
    {
        if (_stream is null) return null;

        // Centralised cache + beacon path. The DashboardFreshnessBridge
        // ticks on Tick10s and invalidates this cache when the catalog
        // size changes; subsequent BuildTileAsync rebuilds via ListAsync.
        // Per feedback_centralised_change_detection.
        var cached = _cache?.TryGet();
        if (cached is not null) return cached;

        var catalog = await _stream.ListAsync(ct).ConfigureAwait(false);
        var count = catalog.Count;

        var tile = new StatTileViewModel(
            Title: "Metrics",
            Value: count.ToString("N0", CultureInfo.InvariantCulture),
            Unit: "meters",
            HealthBand: count > 0 ? HealthBand.Good : HealthBand.Caution,
            DrillHref: DrillPath,
            DrillLabel: "Open insights",
            BeaconKey: DashboardFreshnessBeacon.Surfaces.MeterStreamHealth);

        _cache?.Set(tile);
        return tile;
    }
}
