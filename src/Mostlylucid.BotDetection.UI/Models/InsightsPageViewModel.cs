namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Top-level model for the <c>/dashboard/insights</c> page (Phase B1 of the
///     Pack Metrics + Cohesive Views plan). The page is the "mini-grafana index"
///     -- one row per meter the gateway is currently publishing, with namespace /
///     kind / free-text filters, sortable columns, and a sparkline column driven
///     by the selected window.
///     Composed entirely on the server; <see cref="Table"/> is fed straight to
///     the existing <c>SbMeterTable</c> view component. No JS dependency: the
///     filter strip is a plain <c>&lt;form method="get"&gt;</c> that re-issues
///     the GET.
/// </summary>
/// <param name="Table">Meter rows to render via <c>SbMeterTable</c>.</param>
/// <param name="Filters">Echoed filter state so the form inputs stay sticky.</param>
/// <param name="NamespaceFacets">Top namespaces with counts; current selection flagged.</param>
/// <param name="KindFacets">All meter kinds with counts; current selection flagged.</param>
/// <param name="TotalMeterCount">Total meters known to <c>IMeterStream</c>.</param>
/// <param name="FilteredMeterCount">Rows after filtering.</param>
public sealed record InsightsPageViewModel(
    MeterTableViewModel Table,
    InsightsFilters Filters,
    IReadOnlyList<InsightsFacetOption> NamespaceFacets,
    IReadOnlyList<InsightsFacetOption> KindFacets,
    int TotalMeterCount,
    int FilteredMeterCount);

/// <summary>
///     Echoed filter state for the Insights page form. All optional; nulls mean
///     "no filter applied".
/// </summary>
/// <param name="Kind">MeterKind filter (counter / gauge / histogram / updowncounter).</param>
/// <param name="Namespace">Namespace-prefix filter (case-insensitive contains).</param>
/// <param name="Query">Free-text substring on the meter name.</param>
/// <param name="Sort">Sort key: name / kind / current_desc / current_asc.</param>
/// <param name="Window">Window for the sparkline column: 5m / 15m / 1h.</param>
/// <param name="Buckets">Buckets per series (default 24).</param>
public sealed record InsightsFilters(
    string? Kind,
    string? Namespace,
    string? Query,
    string Sort,
    string Window,
    int Buckets);

/// <summary>
///     A single facet chip on the Insights filter strip. Renders as an
///     <c>&lt;a&gt;</c> linking to the same page with the filter set
///     (or cleared, when <see cref="Selected"/> is true).
/// </summary>
/// <param name="Value">Filter value to put on the query string.</param>
/// <param name="Label">Human-readable label.</param>
/// <param name="Count">Number of meters matching this facet in the unfiltered catalog.</param>
/// <param name="Selected">True when this chip matches the current filter.</param>
public sealed record InsightsFacetOption(
    string Value,
    string Label,
    int Count,
    bool Selected);
