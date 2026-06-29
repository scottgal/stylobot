using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Projects the same <see cref="CountryRow"/> rows that feed the legacy
///     by-country card into a <see cref="ChartletViewModel"/> ready for the
///     shared <c>SbChartlet</c> view component as a horizontal-bar chart.
///     Country code is the bucket label and the single "all traffic" series
///     holds the per-country hit total; click-to-drill wires the bar to the
///     existing <c>?country=&lt;code&gt;</c> filter on the Traffic controller,
///     swapping just the <c>#traffic-panels</c> container via HTMX.
///     <para>
///     No new data store: the rows come from the existing
///     <c>TrafficController.TopByCountry</c> projection.
///     </para>
/// </summary>
public static class CountryChartletBuilder
{
    /// <summary>
    ///     Build a horizontal-bar chartlet from the by-country projection.
    ///     <paramref name="rows"/> is expected to already be in display order
    ///     (top-N by hits) -- the builder preserves order so the rendered
    ///     bars match the legacy card's ranking.
    /// </summary>
    public static ChartletViewModel Build(IReadOnlyList<CountryRow> rows)
    {
        var labels = rows.Select(r => string.IsNullOrEmpty(r.CountryCode) ? "XX" : r.CountryCode).ToArray();
        var buckets = rows.Select(r => (long)r.Hits).ToArray();

        // One neutral series -- per project_dashboard_color_semantics we don't
        // colour-encode "all traffic" by risk, so this uses the elevated token
        // as the dashboard's neutral data-bar colour (no semantic load).
        var series = new List<ChartletSeries>
        {
            new(
                Key: "all",
                Label: "Hits",
                ColorToken: "--sb-color-risk-elevated",
                Buckets: buckets)
        };

        return new ChartletViewModel(
            Id: "traffic-by-country",
            Kind: ChartletKind.HorizontalBar,
            BucketLabels: labels,
            Series: series,
            Axes: new ChartletAxes(YLabel: "country", YFormat: "number", XLabel: "hits", GridLines: false),
            Drill: new ChartletDrill(
                Url: "/dashboard/traffic",
                ParamKey: "country",
                PanelTarget: "#traffic-panels"));
    }
}