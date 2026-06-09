namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for <c>SbSparkline</c> — the pure-SVG single-trend sparkline reused
///     by the cohesive-views widget set (<c>SbStatTile</c>, <c>SbTrendCard</c>,
///     <c>SbMeterTable</c>). All rendering work happens server-side in the partial; no
///     JavaScript is involved.
/// </summary>
/// <param name="Values">The series to plot. Order is left-to-right, oldest-first.</param>
/// <param name="Min">
///     Optional Y-axis minimum. Defaults to the actual minimum of <paramref name="Values"/>.
///     Callers stacking multiple sparklines that need a shared Y axis (e.g. inside
///     <c>SbTrendCard</c>) pass an explicit value.
/// </param>
/// <param name="Max">
///     Optional Y-axis maximum. Defaults to the actual maximum of <paramref name="Values"/>.
/// </param>
/// <param name="ColorClass">
///     CSS token applied to the SVG. The polyline stroke is drawn with
///     <c>currentColor</c>, so the parent's <c>color</c> (driven by this class) tints
///     the line. Reuses existing dashboard tokens — no new CSS rules.
/// </param>
/// <param name="Width">SVG viewBox width in user units.</param>
/// <param name="Height">SVG viewBox height in user units.</param>
public sealed record SparklineViewModel(
    IReadOnlyList<double> Values,
    double? Min = null,
    double? Max = null,
    string ColorClass = "spark-accent",
    int Width = 60,
    int Height = 16);
