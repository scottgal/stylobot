using Mostlylucid.BotDetection.UI.Models;
using Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Projects the same <see cref="BotTypeRow"/> rows that feed the legacy
///     by-bot-type card into a <see cref="ChartletViewModel"/> ready for the
///     shared <c>SbChartlet</c> view component as a horizontal-bar chart. Bot
///     type name is the bucket label; the single series holds per-type hit
///     totals. Click-to-drill wires the bar to the existing
///     <c>?bot_type=&lt;type&gt;</c> filter on the Traffic controller, swapping
///     just the <c>#traffic-panels</c> container via HTMX.
///     <para>
///     No new data store: the rows come from the existing
///     <c>TrafficController.TopByBotType</c> projection.
///     </para>
/// </summary>
public static class BotTypeChartletBuilder
{
    /// <summary>
    ///     Build a horizontal-bar chartlet from the by-bot-type projection.
    ///     <paramref name="rows"/> is expected to already be in display order
    ///     (top-N by hits) -- the builder preserves order so the rendered
    ///     bars match the legacy card's ranking.
    /// </summary>
    public static ChartletViewModel Build(IReadOnlyList<BotTypeRow> rows)
    {
        var labels = rows.Select(r => r.BotType ?? "Unknown").ToArray();
        var buckets = rows.Select(r => (long)r.Hits).ToArray();

        // Chart.js horizontal bars share one colour across the whole series.
        // The semantic colour information lives on the donut and on the
        // headline stacked bar -- this view is the ranked-count cue, so we
        // keep it neutral (elevated token = dashboard's data-bar colour) and
        // let the bar lengths do the work. Per project_dashboard_color_semantics
        // we never reach for primary/secondary/accent on bot-vs-human signals.
        var series = new List<ChartletSeries>
        {
            new(
                Key: "all",
                Label: "Hits",
                ColorToken: "--sb-color-risk-elevated",
                Buckets: buckets)
        };

        return new ChartletViewModel(
            Id: "traffic-by-bot-type",
            Kind: ChartletKind.HorizontalBar,
            BucketLabels: labels,
            Series: series,
            Axes: new ChartletAxes(YLabel: "bot type", YFormat: "number", XLabel: "hits", GridLines: false),
            Drill: new ChartletDrill(
                Url: "/dashboard/traffic",
                ParamKey: "bot_type",
                PanelTarget: "#traffic-panels"));
    }
}