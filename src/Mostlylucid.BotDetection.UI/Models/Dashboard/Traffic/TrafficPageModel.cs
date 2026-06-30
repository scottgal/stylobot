namespace Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

/// <summary>
///     Root model for the Traffic landing page (GA-style overview). T1 wires the
///     URL filter binding, timeseries bucketing, and the five breakdown card
///     projections off <see cref="Services.SignatureAggregateCache"/>. T2 fills the
///     chart partial and T3 fills the per-card partials. The polish pass adds a
///     top-line <see cref="TrafficCounters"/> strip (with prior-window deltas)
///     and a <see cref="BotFamilySeries"/> sub-stack feeding the bar variant of
///     the chart for daily/weekly windows.
/// </summary>
public sealed record TrafficPageModel(
    TrafficFilters Filters,
    TrafficCounters Counters,
    TrafficTimeseries Timeseries,
    BotFamilySeries BotFamilies,
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

/// <summary>
///     Top-line counter strip rendered above the timeseries chart. Counters
///     absorb the "half our traffic disappeared!!!" panic an operator gets when
///     bot blocking lands and the total request count visibly drops; the prior-
///     window deltas show whether the drop is real or just a quieter cohort.
///     Percent deltas are Inf-safe: a prior of 0 yields 100% when the current
///     value is positive and 0% when both are zero.
/// </summary>
public sealed record TrafficCounters(
    int Total,
    int Humans,
    int Bots,
    double BotShare,
    int TotalDelta,
    int HumansDelta,
    int BotsDelta,
    double TotalDeltaPct,
    double HumansDeltaPct,
    double BotsDeltaPct);

/// <summary>
///     Per-family bot sub-stack data feeding the bar chart's bot segment.
///     <see cref="Families"/> holds top-5 family names plus a trailing "Other
///     bots" entry when more than five families appear in the window; the
///     parallel <see cref="PerBucket"/> array is indexed [family][bucket] using
///     the same bucket axis as <see cref="TrafficTimeseries.Buckets"/>.
/// </summary>
public sealed record BotFamilySeries(
    IReadOnlyList<string> Families,
    IReadOnlyList<IReadOnlyList<int>> PerBucket);

public sealed record CountryRow(string CountryCode, int Hits, double BotShare);
public sealed record BotTypeRow(string BotType, int Hits);
/// <summary>
///     One row on the Top Endpoints widget. <see cref="P95Ms"/> + <see cref="ErrorRate"/>
///     come from the same <c>DashboardEndpointStats</c> projection that the
///     load-shed baseline already reads, so there is no parallel atom +
///     no parallel REST endpoint -- this is pure projection on top of the
///     existing IDashboardEventStore surface.
///     <para>
///         <see cref="HasPerf"/> is <c>false</c> when this endpoint's
///         <c>TotalCount</c> is below
///         <see cref="Mostlylucid.BotDetection.UI.Models.Dashboard.Layout.DashboardLayoutOptions.TopEndpointsMinSamplesForPerf"/>;
///         the view renders a dash placeholder in that case so brand-new paths
///         or cold-start windows do not show misleading numbers.
///     </para>
/// </summary>
/// <param name="Method">HTTP method (e.g. <c>GET</c>).</param>
/// <param name="Path">Raw request path (the existing widget links by this).</param>
/// <param name="Hits">Total request count for the window.</param>
/// <param name="BotShare">Bot fraction (0..1) for the window.</param>
/// <param name="P95Ms">95th-percentile processing time in milliseconds.</param>
/// <param name="ErrorRate">Combined 4xx + 5xx fraction (0..1).</param>
/// <param name="HasPerf">
///     <c>true</c> when <see cref="Hits"/> meets
///     <see cref="Mostlylucid.BotDetection.UI.Models.Dashboard.Layout.DashboardLayoutOptions.TopEndpointsMinSamplesForPerf"/>.
/// </param>
public sealed record EndpointRow(
    string Method,
    string Path,
    int Hits,
    double BotShare,
    double P95Ms = 0.0,
    double ErrorRate = 0.0,
    bool HasPerf = false);
public sealed record ThreatRow(string PrimarySignature, string ResolvedName, string ThreatBand, DateTime LastSeen);
