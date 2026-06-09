namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Top-level view model for <c>/dashboard/aspnet-hub</c> -- the FOSS
///     ASP.NET pack hub page (Phase B2 of the Pack Metrics + Cohesive Views
///     plan). One-glance summary of what the AspNet pack is collecting +
///     drill paths into the inventory and live meters.
/// </summary>
/// <param name="Title">Card header title (typically "ASP.NET Pack").</param>
/// <param name="Subtitle">Card header subtitle.</param>
/// <param name="OverallHealthBand">
///     Page-level health: Good when both inventory and meter ingest are
///     populated; Caution when one is empty; Warning when both are empty.
/// </param>
/// <param name="TopTiles">
///     Stat-tile strip rendered top-of-page. Order is render order. Tiles
///     come pre-formatted so the partial doesn't do any formatting itself.
/// </param>
/// <param name="IngestTrend">
///     Single trend card showing the pack's observations-received counter
///     over the last 1h. Null when the pack hasn't booted yet (no
///     observations counter found).
/// </param>
/// <param name="MeterTable">
///     Full meter table -- one row per AspNet-namespaced meter the gateway
///     is publishing. EmptyMessage is populated when no AspNet meters exist
///     so the partial doesn't have to special-case the empty path.
/// </param>
public sealed record AspNetPackHubViewModel(
    string Title,
    string Subtitle,
    HealthBand OverallHealthBand,
    IReadOnlyList<StatTileViewModel> TopTiles,
    TrendCardViewModel? IngestTrend,
    MeterTableViewModel MeterTable);

/// <summary>
///     Machine-readable rollup for <c>GET /api/v1/packs/aspnet/summary</c>.
///     Mirrors the view-model surface in number form so fleet-rollup
///     consumers (and the commercial overlay) can pull the same numbers
///     the dashboard renders without scraping HTML.
/// </summary>
/// <param name="EndpointCount">
///     Total endpoints in the in-process ASP.NET inventory. Null when the
///     viewer host has no <c>IAspNetEndpointInventory</c> registered
///     (Remote-mode pattern -- pulls via REST instead).
/// </param>
/// <param name="MeterFamilyCount">
///     Number of meters currently published in the "aspnet" namespace.
/// </param>
/// <param name="ObservationsLast15m">
///     Sum of the AspNet pack's observations-received counter over the
///     last 15 minutes. Null when no such counter has been observed yet
///     (pack not booted / not yet receiving traffic).
/// </param>
/// <param name="InferredEndpointsLast15m">
///     Sum of the inferred-endpoints counter over the last 15 minutes.
///     Null when the pack doesn't publish such a counter.
/// </param>
/// <param name="HealthBand">
///     Same overall health classification as the view model header.
/// </param>
/// <param name="ComputedAtUtc">
///     Server timestamp at which the summary was computed.
/// </param>
public sealed record AspNetPackSummary(
    int? EndpointCount,
    int MeterFamilyCount,
    double? ObservationsLast15m,
    double? InferredEndpointsLast15m,
    HealthBand HealthBand,
    DateTimeOffset ComputedAtUtc);
