namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for <c>SbTrendCard</c> -- the medium-sized meter card used on
///     per-pack hub pages and on the dashboard overview's pack-health row when a
///     single number isn't enough. Sits between the compact <see cref="StatTileViewModel"/>
///     and a full chart panel: headline value + optional inline sparkline next to
///     the headline + a wider trend sparkline below + optional axis ticks + optional
///     drill link.
/// </summary>
/// <param name="Title">Card title (required).</param>
/// <param name="Value">
///     Pre-formatted display value (required). Mirrors <see cref="StatTileViewModel"/>:
///     the widget renders the string verbatim so callers own the formatting
///     ("38", "12.4k", "p99 2.1ms", etc.).
/// </param>
/// <param name="Subtitle">
///     Optional small line under the title -- typically meter family, namespace,
///     or unit context (e.g. "stylobot_aspnet observations / 1m").
/// </param>
/// <param name="Unit">Optional unit hint shown next to the value (e.g. "ms", "/s").</param>
/// <param name="Delta">
///     Optional headline delta annotation (e.g. "+12 vs 5m"). Pre-formatted; the
///     widget does not compute differences or choose a sign.
/// </param>
/// <param name="HealthBand">
///     Drives the left-border accent colour and renders an <c>SbHealthDot</c>
///     next to the title for non-<see cref="Models.HealthBand.Neutral"/> values.
/// </param>
/// <param name="HeadlineSparkline">
///     Optional small sparkline rendered inline next to the headline value. Use
///     this when the headline number alone doesn't communicate trend at a glance.
/// </param>
/// <param name="TrendSparkline">
///     Optional wider sparkline rendered below the headline, showing the
///     longer-window time series.
/// </param>
/// <param name="AxisLabels">
///     Optional axis tick labels rendered under the trend sparkline
///     (e.g. ["-30m","-15m","now"]).
/// </param>
/// <param name="DrillHref">Optional anchor URL for the footer drill link.</param>
/// <param name="DrillLabel">
///     Optional drill link label. Defaults to "Details" when an href is supplied
///     but no label is.
/// </param>
/// <param name="Icon">Optional CSS icon class from the existing icon set.</param>
public sealed record TrendCardViewModel(
    string Title,
    string Value,
    string? Subtitle = null,
    string? Unit = null,
    string? Delta = null,
    HealthBand HealthBand = HealthBand.Neutral,
    SparklineViewModel? HeadlineSparkline = null,
    SparklineViewModel? TrendSparkline = null,
    IReadOnlyList<string>? AxisLabels = null,
    string? DrillHref = null,
    string? DrillLabel = null,
    string? Icon = null);
