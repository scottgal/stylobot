using Mostlylucid.BotDetection.UI.Models;

namespace Mostlylucid.BotDetection.UI.Dashboard.Composition;

/// <summary>
///     Marker types registering the "live-time-overlay" / "live-endpoints-overlay" widget
///     keys in the <see cref="DashboardWidgetCatalog"/> — same pattern as
///     <see cref="TrafficTimeChartWidget"/>. Neither overlay is its own rendered widget; the
///     Traffic page's chart and endpoints list merge these slices in over their base data
///     (see <c>TrafficController.Index</c> / <c>MergeLiveTimeSeries</c>). These
///     classes exist solely so <see cref="DashboardWidgetCatalog.BuildFromLoadedAssemblies"/>
///     discovers <see cref="DatasetKind.LiveTimeBuckets"/> / <see cref="DatasetKind.LiveEndpointStats"/>
///     as part of the SAME <c>ComposeBatchAsync</c> call the rest of the traffic manifest
///     issues (render-once ruling, 2026-08-22) instead of two extra per-render round trips.
/// </summary>
[DashboardWidget("live-time-overlay", DatasetKind.LiveTimeBuckets)]
internal sealed class TrafficLiveTimeOverlayWidget;

[DashboardWidget("live-endpoints-overlay", DatasetKind.LiveEndpointStats)]
internal sealed class TrafficLiveEndpointsOverlayWidget;
