using System.Globalization;
using Mostlylucid.BotDetection.AspNetPack.Inventory;
using Mostlylucid.BotDetection.PrometheusPack.Telemetry;
using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Services;

namespace Mostlylucid.BotDetection.AspNetPack.Services;

/// <summary>
///     Builder for the <c>/dashboard/aspnet-hub</c> page (B2 of the Pack
///     Metrics + Cohesive Views plan) and its companion JSON summary
///     endpoint at <c>GET /api/v1/packs/aspnet/summary</c>.
///     <para>
///         Pure logic: takes an <see cref="IMeterStream"/> (always present
///         when the dashboard renders the page) and an optional
///         <see cref="IAspNetEndpointInventory"/> (absent on viewer hosts
///         that pull data via REST -- the Remote-mode pattern), and returns
///         a fully composed view model. No MVC dependency so the same code is
///         exercised from the dashboard middleware and from unit tests
///         directly.
///     </para>
///     <para>
///         Per <c>project_gateway_data_locality</c>: this code never touches
///         the DB; the telemetry path goes through <see cref="IMeterStream"/>
///         only and the inventory path is the in-process EndpointDataSource
///         enumeration.
///     </para>
/// </summary>
public sealed class AspNetPackHubPageBuilder : IAspNetPackHubBuilder
{
    /// <summary>
    ///     Window used for the "observations / 15m" tile and the inferred-
    ///     endpoints tile. Same window the dashboard plan calls out so the
    ///     numbers shown on the page match those returned by the JSON
    ///     summary endpoint.
    /// </summary>
    public static readonly TimeSpan ShortWindow = TimeSpan.FromMinutes(15);

    /// <summary>
    ///     Window used for the ingest trend card. The card axis labels
    ///     ("-60m", "-30m", "now") reflect this window directly.
    /// </summary>
    public static readonly TimeSpan TrendWindow = TimeSpan.FromHours(1);

    /// <summary>Bucket count used for the short-window sparkline column.</summary>
    public const int ShortWindowBuckets = 24;

    /// <summary>Bucket count used for the trend card sparkline.</summary>
    public const int TrendBuckets = 24;

    /// <summary>
    ///     Substring matched against meter names + namespaces to decide
    ///     "this is an AspNet pack meter". Matches the namespace string the
    ///     pack uses today plus the underscore-prefixed convention so the
    ///     page survives a future namespace rename in the pack without
    ///     code churn.
    /// </summary>
    public const string AspNetNamespace = "aspnet";

    /// <summary>
    ///     Substring matched against meter names to identify the pack's
    ///     observations-received counter. Picks the first counter whose
    ///     name contains both <see cref="AspNetNamespace"/> and "observations"
    ///     so the page survives the pack ranaming the metric (e.g. from
    ///     <c>stylobot_aspnet_observations_received_total</c> to anything
    ///     similarly shaped).
    /// </summary>
    public const string ObservationsToken = "observations";

    /// <summary>
    ///     Substring matched against meter names to identify the pack's
    ///     inferred-endpoints counter. Optional -- when no such counter
    ///     exists the corresponding tile is omitted entirely rather than
    ///     fabricated.
    /// </summary>
    public const string InferredEndpointsToken = "inferred";

    private readonly IMeterStream _stream;
    private readonly IAspNetEndpointInventory? _inventory;
    private readonly TimeProvider _time;

    public AspNetPackHubPageBuilder(
        IMeterStream stream,
        IAspNetEndpointInventory? inventory = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        _inventory = inventory;
        _time = time ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<AspNetPackHubViewModel> BuildAsync(CancellationToken ct)
    {
        var snapshot = await BuildSnapshotAsync(ct).ConfigureAwait(false);

        var topTiles = BuildTopTiles(snapshot);
        var trend = BuildIngestTrend(snapshot);
        var table = BuildMeterTable(snapshot);

        return new AspNetPackHubViewModel(
            Title: "ASP.NET Pack",
            Subtitle: "Endpoint inventory + ASP.NET-namespace meter rollups",
            OverallHealthBand: snapshot.OverallHealth,
            TopTiles: topTiles,
            IngestTrend: trend,
            MeterTable: table);
    }

    /// <inheritdoc />
    public async Task<AspNetPackSummary> BuildSummaryAsync(CancellationToken ct)
    {
        var snapshot = await BuildSnapshotAsync(ct).ConfigureAwait(false);
        return new AspNetPackSummary(
            EndpointCount: snapshot.EndpointCount,
            MeterFamilyCount: snapshot.AspNetMeters.Count,
            ObservationsLast15m: snapshot.ObservationsLast15m,
            InferredEndpointsLast15m: snapshot.InferredEndpointsLast15m,
            HealthBand: snapshot.OverallHealth,
            ComputedAtUtc: _time.GetUtcNow());
    }

    // ----- snapshot composition ------------------------------------------

    private async Task<Snapshot> BuildSnapshotAsync(CancellationToken ct)
    {
        int? endpointCount = _inventory is null ? null : _inventory.All().Count;

        var catalog = await _stream.ListAsync(ct).ConfigureAwait(false);
        var aspnet = FilterAspNet(catalog);

        // Time-series per AspNet meter -- one short-window pull for the table
        // row and the observations tile and one long-window pull for the
        // trend card. Cached locally so a meter only gets a network/DI call
        // once per build.
        var shortSeries = new Dictionary<string, MeterTimeSeries?>(StringComparer.Ordinal);
        foreach (var entry in aspnet)
        {
            ct.ThrowIfCancellationRequested();
            shortSeries[entry.Name] = await _stream
                .GetAsync(entry.Name, ShortWindow, ShortWindowBuckets, ct)
                .ConfigureAwait(false);
        }

        var observations = SelectObservationsCounter(aspnet);
        var inferred = SelectInferredCounter(aspnet);

        MeterTimeSeries? observationsTrend = null;
        if (observations is not null)
        {
            observationsTrend = await _stream
                .GetAsync(observations.Name, TrendWindow, TrendBuckets, ct)
                .ConfigureAwait(false);
        }

        double? observationsTotal = observations is not null
            ? SumValues(shortSeries.GetValueOrDefault(observations.Name))
            : null;

        double? inferredTotal = inferred is not null
            ? SumValues(shortSeries.GetValueOrDefault(inferred.Name))
            : null;

        var hasMeterIngest = aspnet.Count > 0 && shortSeries.Values.Any(HasSamples);
        var hasInventory = endpointCount is > 0;

        return new Snapshot(
            EndpointCount: endpointCount,
            AspNetMeters: aspnet,
            ShortSeries: shortSeries,
            ObservationsCounter: observations,
            ObservationsTrend: observationsTrend,
            ObservationsLast15m: observationsTotal,
            InferredCounter: inferred,
            InferredEndpointsLast15m: inferredTotal,
            OverallHealth: ComputeOverallHealth(hasInventory, hasMeterIngest));
    }

    // ----- tile / card / table builders ----------------------------------

    private IReadOnlyList<StatTileViewModel> BuildTopTiles(Snapshot snapshot)
    {
        var tiles = new List<StatTileViewModel>(4);

        // Tile 1 -- Endpoints inventoried.
        tiles.Add(snapshot.EndpointCount is null
            ? new StatTileViewModel(
                Title: "Endpoints",
                Value: "—",
                DrillHref: "/dashboard/aspnet-pack/routes",
                DrillLabel: "Browse routes",
                HealthBand: HealthBand.Caution)
            : new StatTileViewModel(
                Title: "Endpoints",
                Value: snapshot.EndpointCount.Value.ToString("N0", CultureInfo.InvariantCulture),
                DrillHref: "/dashboard/aspnet-pack/routes",
                DrillLabel: "Browse routes",
                HealthBand: snapshot.EndpointCount.Value > 0 ? HealthBand.Good : HealthBand.Caution));

        // Tile 2 -- Meter families published.
        tiles.Add(new StatTileViewModel(
            Title: "Meters",
            Value: snapshot.AspNetMeters.Count.ToString("N0", CultureInfo.InvariantCulture),
            DrillHref: $"/dashboard/insights?namespace={AspNetNamespace}",
            DrillLabel: "View meters",
            HealthBand: snapshot.AspNetMeters.Count > 0 ? HealthBand.Good : HealthBand.Caution));

        // Tile 3 -- Observations received (15m).
        if (snapshot.ObservationsCounter is not null)
        {
            var ts = snapshot.ShortSeries.GetValueOrDefault(snapshot.ObservationsCounter.Name);
            var spark = ts is not null && ts.Values.Count > 0
                ? new SparklineViewModel(ts.Values, ColorClass: "spark-accent")
                : null;
            tiles.Add(new StatTileViewModel(
                Title: "Observations / 15m",
                Value: FormatNumber(snapshot.ObservationsLast15m ?? 0d),
                Sparkline: spark,
                HealthBand: HealthBand.Neutral,
                DrillHref: $"/dashboard/insights?q={Uri.EscapeDataString(snapshot.ObservationsCounter.Name)}",
                DrillLabel: "Open meter"));
        }
        else
        {
            tiles.Add(new StatTileViewModel(
                Title: "Observations / 15m",
                Value: "—",
                HealthBand: HealthBand.Caution));
        }

        // Tile 4 -- Inferred endpoints written (15m). Omitted when the pack
        // doesn't publish such a counter (the spec is explicit: do not
        // fabricate a meter name).
        if (snapshot.InferredCounter is not null)
        {
            var ts = snapshot.ShortSeries.GetValueOrDefault(snapshot.InferredCounter.Name);
            var spark = ts is not null && ts.Values.Count > 0
                ? new SparklineViewModel(ts.Values, ColorClass: "spark-accent")
                : null;
            tiles.Add(new StatTileViewModel(
                Title: "Inferred endpoints / 15m",
                Value: FormatNumber(snapshot.InferredEndpointsLast15m ?? 0d),
                Sparkline: spark,
                HealthBand: HealthBand.Neutral,
                DrillHref: $"/dashboard/insights?q={Uri.EscapeDataString(snapshot.InferredCounter.Name)}",
                DrillLabel: "Open meter"));
        }

        return tiles;
    }

    private TrendCardViewModel? BuildIngestTrend(Snapshot snapshot)
    {
        // No observations counter at all -- render a placeholder card so the
        // operator sees "we know this surface should exist; it hasn't booted
        // yet" rather than a blank gap on the page.
        if (snapshot.ObservationsCounter is null)
        {
            return new TrendCardViewModel(
                Title: "Pack ingest trend (1h)",
                Value: "—",
                Subtitle: "Pack telemetry not yet observed",
                HealthBand: HealthBand.Caution);
        }

        var trend = snapshot.ObservationsTrend;
        SparklineViewModel? headline = trend is not null && trend.Values.Count > 0
            ? new SparklineViewModel(trend.Values, ColorClass: "spark-accent")
            : null;
        SparklineViewModel? wide = trend is not null && trend.Values.Count > 0
            ? new SparklineViewModel(trend.Values, ColorClass: "spark-accent", Width: 240, Height: 36)
            : null;

        return new TrendCardViewModel(
            Title: "Pack ingest trend (1h)",
            Value: FormatNumber(trend?.Current ?? 0d),
            Subtitle: snapshot.ObservationsCounter.Name,
            HealthBand: HealthBand.Neutral,
            HeadlineSparkline: headline,
            TrendSparkline: wide,
            AxisLabels: new[] { "-60m", "-30m", "now" },
            DrillHref: $"/dashboard/insights?q={Uri.EscapeDataString(snapshot.ObservationsCounter.Name)}&window=1h",
            DrillLabel: "Open meter");
    }

    private MeterTableViewModel BuildMeterTable(Snapshot snapshot)
    {
        var rows = new List<MeterTableRowViewModel>(snapshot.AspNetMeters.Count);
        foreach (var entry in snapshot.AspNetMeters)
        {
            var ts = snapshot.ShortSeries.GetValueOrDefault(entry.Name);
            rows.Add(BuildRow(entry, ts));
        }

        rows = rows
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new MeterTableViewModel(
            Title: "All ASP.NET pack meters",
            Rows: rows,
            EmptyMessage: "No ASP.NET pack meters observed yet.",
            Caption: rows.Count == 1 ? "1 meter" : $"{rows.Count} meters");
    }

    internal static MeterTableRowViewModel BuildRow(MeterCatalogEntry entry, MeterTimeSeries? ts)
    {
        var values = ts?.Values ?? Array.Empty<double>();
        SparklineViewModel? spark = values.Count > 0
            ? new SparklineViewModel(values, ColorClass: ColorForKind(entry.Kind))
            : null;
        var current = ts is null ? "—" : FormatNumber(ts.Current);

        return new MeterTableRowViewModel(
            Name: entry.Name,
            Kind: entry.Kind.ToString(),
            Value: current,
            Unit: entry.Unit,
            Sparkline: spark,
            HealthBand: HealthBand.Neutral,
            DrillHref: $"/dashboard/insights/{Uri.EscapeDataString(entry.Name)}",
            Description: entry.Description);
    }

    // ----- filter / selection helpers ------------------------------------

    internal static List<MeterCatalogEntry> FilterAspNet(IReadOnlyList<MeterCatalogEntry> catalog)
    {
        var result = new List<MeterCatalogEntry>(catalog.Count);
        foreach (var entry in catalog)
        {
            if (IsAspNetMeter(entry)) result.Add(entry);
        }
        return result;
    }

    internal static bool IsAspNetMeter(MeterCatalogEntry entry)
    {
        if (!string.IsNullOrEmpty(entry.Namespace)
            && entry.Namespace.Contains(AspNetNamespace, StringComparison.OrdinalIgnoreCase))
            return true;
        // Conservative fallback: the meter publisher may declare a different
        // namespace shape (e.g. <c>stylobot</c>) while the instrument name
        // itself carries the pack tag. Match both
        // <c>stylobot_aspnet_*</c>-style and <c>aspnet.*</c>-style names.
        return entry.Name.Contains("aspnet", StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains(".aspnet.", StringComparison.OrdinalIgnoreCase)
            || entry.Name.Contains("_aspnet_", StringComparison.OrdinalIgnoreCase);
    }

    internal static MeterCatalogEntry? SelectObservationsCounter(IReadOnlyList<MeterCatalogEntry> aspnet)
    {
        // Prefer the most "directly named" observations counter when several
        // match. Order: exact "observations_received" / "observations.received"
        // hits, then any meter containing the observations token.
        MeterCatalogEntry? best = null;
        foreach (var e in aspnet)
        {
            if (e.Kind != MeterKind.Counter) continue;
            if (!e.Name.Contains(ObservationsToken, StringComparison.OrdinalIgnoreCase)) continue;

            if (e.Name.Contains("received", StringComparison.OrdinalIgnoreCase))
                return e;

            best ??= e;
        }
        return best;
    }

    internal static MeterCatalogEntry? SelectInferredCounter(IReadOnlyList<MeterCatalogEntry> aspnet)
    {
        foreach (var e in aspnet)
        {
            if (e.Kind != MeterKind.Counter) continue;
            if (e.Name.Contains(InferredEndpointsToken, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    internal static HealthBand ComputeOverallHealth(bool hasInventory, bool hasMeterIngest)
    {
        return (hasInventory, hasMeterIngest) switch
        {
            (true,  true)  => HealthBand.Good,
            (false, false) => HealthBand.Warning,
            _              => HealthBand.Caution,
        };
    }

    private static bool HasSamples(MeterTimeSeries? ts)
    {
        if (ts is null) return false;
        foreach (var v in ts.Values)
        {
            if (v != 0d) return true;
        }
        return ts.Current != 0d;
    }

    private static double SumValues(MeterTimeSeries? ts)
    {
        if (ts is null) return 0d;
        double total = 0d;
        foreach (var v in ts.Values) total += v;
        return total;
    }

    internal static string FormatNumber(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) return "—";
        var abs = Math.Abs(value);

        if (abs > 1_000_000)
        {
            var m = value / 1_000_000d;
            return m.ToString(Math.Abs(m) < 10 ? "0.0" : "0", CultureInfo.InvariantCulture) + "M";
        }
        if (abs > 100_000)
        {
            var k = value / 1_000d;
            return k.ToString("0", CultureInfo.InvariantCulture) + "K";
        }
        if (abs > 9_999)
        {
            return value.ToString("N0", CultureInfo.InvariantCulture);
        }
        if (Math.Abs(value - Math.Round(value)) < 0.0001)
            return value.ToString("0", CultureInfo.InvariantCulture);
        return value.ToString("0.##", CultureInfo.InvariantCulture);
    }

    internal static string ColorForKind(MeterKind kind) => kind switch
    {
        MeterKind.Counter       => "spark-accent",
        MeterKind.Gauge         => "spark-good",
        MeterKind.Histogram     => "spark-caution",
        MeterKind.UpDownCounter => "spark-info",
        _                       => "spark-accent",
    };

    // ----- internal snapshot record --------------------------------------

    private sealed record Snapshot(
        int? EndpointCount,
        IReadOnlyList<MeterCatalogEntry> AspNetMeters,
        IReadOnlyDictionary<string, MeterTimeSeries?> ShortSeries,
        MeterCatalogEntry? ObservationsCounter,
        MeterTimeSeries? ObservationsTrend,
        double? ObservationsLast15m,
        MeterCatalogEntry? InferredCounter,
        double? InferredEndpointsLast15m,
        HealthBand OverallHealth);
}