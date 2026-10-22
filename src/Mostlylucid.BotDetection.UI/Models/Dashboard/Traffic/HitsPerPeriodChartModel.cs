namespace Mostlylucid.BotDetection.UI.Models.Dashboard.Traffic;

/// <summary>
///     Model for the hits-per-period chart widget (<c>_HitsPerPeriodChart.cshtml</c>).
///     <para>
///         Exists so the SAME markup serves the SSR first paint and the beacon's OOB
///         re-render: <c>_Body.cshtml</c> renders it with the page's composed timeseries,
///         and <c>SbWidgetBatchMiddleware.RenderTimeChartAsync</c> renders it from the warm
///         page bundle's TimeBuckets slice. One partial, one widget contract
///         (<c>data-sb-widget="time-chart"</c>), so a beacon swap can never drift from the
///         markup the client is swapping out (feedback_never_two_sources_of_truth).
///     </para>
/// </summary>
/// <param name="Chart">The Chart.js view model (labels + per-audience series).</param>
/// <param name="Label">Card heading, e.g. "Hits per period (last 24h)".</param>
/// <param name="Window">Window token (15m/1h/6h/24h/7d/30d/custom) — used for the
///     warming copy and the <c>data-sb-params</c> the bridge forwards on refresh, so a
///     beacon re-render reads the SAME window the page is showing.</param>
/// <param name="IsWarming">True on a cold envelope (no composed TimeBuckets yet) — the
///     widget renders the honest "generating" state, and the beacon replaces it once the
///     bundle warms.</param>
/// <param name="CompletenessNote">
///     Optional restrained note about how complete the plotted history is for the
///     active scope (e.g. "stored history only while a domain filter is active").
///     Null renders nothing; the chart never implies live freshness it does not have.
/// </param>
public sealed record HitsPerPeriodChartModel(
    ChartletViewModel Chart,
    string Label,
    string Window,
    bool IsWarming,
    string? CompletenessNote = null)
{
    /// <summary>
    ///     Honest, traffic-specific empty sentence. Deliberately NOT the endpoint
    ///     table's "no endpoint breakdown" wording: this chart plots the traffic time
    ///     series (summary buckets), and its emptiness means no traffic history was
    ///     recorded for the range — not that the endpoint rollup is partial. The card,
    ///     axes and legend stay visible around it (zero axis, no fabricated series).
    /// </summary>
    public const string NoTrafficText = "No recorded traffic history in this range yet.";
}
