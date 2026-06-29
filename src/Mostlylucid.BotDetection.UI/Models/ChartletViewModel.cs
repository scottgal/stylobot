namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     The chart families a chartlet can render. Names are serialised verbatim
///     to JSON (as strings, not ints) so the client-side <c>sb-chartlet.js</c>
///     primitive can switch on the value. Add new kinds here when a new chart
///     shape is needed across the dashboard.
/// </summary>
public enum ChartletKind
{
    StackedBar,
    StackedArea,
    HorizontalBar,
    Donut,
    Line
}

/// <summary>
///     Axis configuration for a chartlet. Both labels are plain text rendered
///     by the partial; <see cref="YFormat"/> picks the value formatter on the
///     client (e.g. <c>"number"</c>, <c>"ms"</c>, <c>"percent"</c>).
/// </summary>
/// <param name="YLabel">Plain-text Y axis label (e.g. "hits", "p95 ms").</param>
/// <param name="YFormat">Formatter token consumed by sb-chartlet.js.</param>
/// <param name="XLabel">Plain-text X axis label (e.g. "time", "endpoint").</param>
/// <param name="GridLines">Whether to render the value-axis grid lines.</param>
public sealed record ChartletAxes(
    string YLabel,
    string YFormat,
    string XLabel,
    bool GridLines);

/// <summary>
///     One series in a chartlet -- a labelled bucket array. Every series in a
///     given <see cref="ChartletViewModel"/> must share the same bucket count
///     as the parent's <see cref="ChartletViewModel.BucketLabels"/>.
/// </summary>
/// <param name="Key">Stable identifier used for drill links and DOM keys.</param>
/// <param name="Label">Human-readable legend label.</param>
/// <param name="ColorToken">
///     CSS custom-property token (e.g. <c>--sb-danger</c>) the client resolves
///     against the active theme. Keeps colour ownership in CSS, not view code.
/// </param>
/// <param name="Buckets">
///     Bucket values, oldest-first. Must align with the parent
///     <see cref="ChartletViewModel.BucketLabels"/> by index.
/// </param>
public sealed record ChartletSeries(
    string Key,
    string Label,
    string ColorToken,
    IReadOnlyList<long> Buckets);

/// <summary>
///     Drill-down target for a chartlet. When non-null, the chartlet binds a
///     click handler that issues an HTMX GET to <see cref="Url"/> with the
///     selected series' <see cref="ChartletSeries.Key"/> as the
///     <see cref="ParamKey"/> query parameter, swapping the resulting markup
///     into <see cref="PanelTarget"/>.
/// </summary>
/// <param name="Url">Drill target URL (server-rendered partial).</param>
/// <param name="ParamKey">Query parameter name the target reads.</param>
/// <param name="PanelTarget">CSS selector for the panel that receives the swap.</param>
public sealed record ChartletDrill(
    string Url,
    string ParamKey,
    string PanelTarget);

/// <summary>
///     Server-side view model backing every chart on the dashboard. One shape,
///     one partial (<c>SbChartlet</c>), one JS primitive (<c>sb-chartlet.js</c>).
///     Each chart instance is built by a small per-chart builder that produces
///     this record; the partial does the rest.
/// </summary>
/// <param name="Id">DOM id / stable handle for the chart instance.</param>
/// <param name="Kind">Chart family -- drives the renderer branch on the client.</param>
/// <param name="BucketLabels">
///     X-axis bucket labels, oldest-first. Every series' bucket array must
///     match this length.
/// </param>
/// <param name="Series">One or more named series to plot.</param>
/// <param name="Axes">Axis configuration shared by all series.</param>
/// <param name="Drill">
///     Optional drill-down config. Null means the chartlet is read-only.
/// </param>
public sealed record ChartletViewModel(
    string Id,
    ChartletKind Kind,
    IReadOnlyList<string> BucketLabels,
    IReadOnlyList<ChartletSeries> Series,
    ChartletAxes Axes,
    ChartletDrill? Drill);
