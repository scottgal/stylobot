using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     Marker type that registers the "time-chart" widget key → <see cref="DatasetKind.TimeBuckets"/>
///     in the <see cref="DashboardWidgetCatalog"/>.  The time-series data for the traffic page
///     is not rendered by a standalone ViewComponent; it is built inline in
///     <c>TrafficController</c> via <c>HitsPerPeriodChartletBuilder.BuildSeries</c>.
///     This class exists solely so the catalog discovers the mapping at startup
///     (via <see cref="DashboardWidgetCatalog.BuildFromLoadedAssemblies"/>).
/// </summary>
[DashboardWidget("time-chart", DatasetKind.TimeBuckets)]
internal sealed class TrafficTimeChartWidget;
