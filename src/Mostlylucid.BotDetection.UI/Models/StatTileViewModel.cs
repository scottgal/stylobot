namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for <c>SbStatTile</c> -- the big-number rollup card used by
///     dashboard hub headers and pack summary rows. All optional props compose; a
///     tile with just a title + value still renders cleanly.
/// </summary>
/// <param name="Title">Card title (e.g. "Endpoints", "Bots last 24h").</param>
/// <param name="Value">
///     Pre-formatted display value -- the tile does not format numbers itself
///     because callers already know whether they want "38", "12.4k", "p99 2.1ms",
///     etc.
/// </param>
/// <param name="Unit">Optional unit hint shown next to the value (e.g. "ms", "/s").</param>
/// <param name="Sparkline">
///     Optional embedded sparkline. Composed via <c>Component.InvokeAsync</c> so
///     the SVG geometry stays owned by <c>SbSparkline</c> and never gets reimplemented.
/// </param>
/// <param name="Delta">
///     Optional delta annotation rendered under the value (e.g. "+12 vs 24h ago").
///     Pre-formatted; the tile does not pick a sign or compute differences.
/// </param>
/// <param name="HealthBand">
///     Drives the left-border accent colour via the same tokens <see cref="HealthDotViewModel"/>
///     uses, so health is visible at a glance without an explicit dot.
/// </param>
/// <param name="DrillHref">Optional anchor URL for the footer drill link.</param>
/// <param name="DrillLabel">Optional drill link text. Defaults to "View" when an href is supplied but no label is.</param>
/// <param name="Icon">Optional CSS icon class from the existing icon set (e.g. boxicons).</param>
public sealed record StatTileViewModel(
    string Title,
    string Value,
    string? Unit = null,
    SparklineViewModel? Sparkline = null,
    string? Delta = null,
    HealthBand HealthBand = HealthBand.Neutral,
    string? DrillHref = null,
    string? DrillLabel = null,
    string? Icon = null);
