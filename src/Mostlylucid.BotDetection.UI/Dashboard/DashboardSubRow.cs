namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     A sub-row contributed by an <see cref="IDashboardPack" />. Each sub-row
///     is rendered as a clickable link under its parent pack header in the left
///     nav, and dispatches to a view component when its route is hit.
/// </summary>
/// <param name="Id">Path-safe slug used as the URL segment (e.g. "log-sink").</param>
/// <param name="Label">Display label shown in the nav (e.g. "Log sink").</param>
/// <param name="ViewComponentName">
///     View component name resolved at dispatch via
///     <c>Component.InvokeAsync(...)</c> (e.g. "SbAspNetLogSink").
/// </param>
public sealed record DashboardSubRow(
    string Id,
    string Label,
    string ViewComponentName);
