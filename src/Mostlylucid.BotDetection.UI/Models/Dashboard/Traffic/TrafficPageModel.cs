namespace Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

/// <summary>
///     Root model for the Traffic landing page (GA-style overview). T1 wires the
///     URL filter binding, timeseries bucketing, and the five breakdown card
///     projections off <see cref="Services.SignatureAggregateCache"/>. T2 fills the
///     chart partial and T3 fills the per-card partials.
/// </summary>
public sealed record TrafficPageModel(
    TrafficFilters Filters,
    TrafficTimeseries Timeseries,
    IReadOnlyList<CountryRow> Countries,
    IReadOnlyList<BotTypeRow> BotTypes,
    IReadOnlyList<EndpointRow> TopEndpoints,
    IReadOnlyList<CachedVisitor> TopVisitors,
    IReadOnlyList<ThreatRow> Threats);

/// <summary>
///     URL-bound filter set. Empty / null values mean "no filter on this axis".
///     <see cref="Window"/> falls back to the configured DefaultTimeWindowMinutes
///     when the URL omits it.
/// </summary>
public sealed record TrafficFilters(
    string? Country = null,
    string? BotType = null,
    string Window = "60m",
    string? Threat = null);

/// <summary>
///     Three parallel int[] series sharing one DateTime[] bucket axis. The
///     hits-per-bucket counts split by inferred audience (Human / Suspicious /
///     Bot) using the BotProbability thresholds at the project's spec: &lt; 0.3
///     human, 0.3-0.8 suspicious, &gt;= 0.8 bot.
/// </summary>
public sealed record TrafficTimeseries(
    IReadOnlyList<DateTime> Buckets,
    IReadOnlyList<int> Human,
    IReadOnlyList<int> Suspicious,
    IReadOnlyList<int> Bot);

public sealed record CountryRow(string CountryCode, int Hits, double BotShare);
public sealed record BotTypeRow(string BotType, int Hits);
public sealed record EndpointRow(string Method, string Path, int Hits, double BotShare);
public sealed record ThreatRow(string PrimarySignature, string ResolvedName, string ThreatBand, DateTime LastSeen);
