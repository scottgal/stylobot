namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for a tabular meter listing -- one row per <c>IMeterStream</c> meter,
///     with the sparkline in a dedicated column so trend is visible without breaking the
///     table grid. No FOSS view renders this directly (the FOSS-only
///     <c>/dashboard/insights</c> page that used to was removed as dead code -- its route
///     permanently redirects to <c>/traffic</c>); the type itself stays because the
///     commercial AspNetPack's per-pack metrics hub
///     (<c>Stylobot.Commercial.AspNetPack.Ui.AspNetMetricsViewModelBuilder</c>,
///     <c>AspNetPackHubPageBuilder</c>) builds and renders it directly against its own view.
/// </summary>
/// <param name="Title">Section title rendered above the table.</param>
/// <param name="Rows">Meter rows, in render order.</param>
/// <param name="EmptyMessage">
///     Optional message displayed in place of the table when <paramref name="Rows"/>
///     is empty. Defaults to "No meters".
/// </param>
/// <param name="Caption">Optional short caption rendered under the title.</param>
public sealed record MeterTableViewModel(
    string Title,
    IReadOnlyList<MeterTableRowViewModel> Rows,
    string? EmptyMessage = null,
    string? Caption = null);

/// <summary>
///     A single row in an <see cref="MeterTableViewModel"/>. Each row represents
///     one meter from <c>IMeterStream</c>; the caller pre-formats the value and
///     picks the kind/health classifications so the widget stays presentational.
/// </summary>
/// <param name="Name">Meter family name (typically already namespaced).</param>
/// <param name="Kind">
///     Meter kind label -- one of "Counter", "Gauge", "Histogram", or
///     "UpDownCounter". Lower-cased and used as the badge class suffix
///     (e.g. <c>sb-meter-table__pill--counter</c>).
/// </param>
/// <param name="Value">Pre-formatted current value string.</param>
/// <param name="Unit">Optional unit hint shown next to the value.</param>
/// <param name="Sparkline">
///     Optional inline sparkline rendered in the trend column. When omitted, the
///     cell renders empty so the column structure stays consistent across rows.
/// </param>
/// <param name="HealthBand">
///     Drives the row's left-border accent and renders an <c>SbHealthDot</c>
///     next to the name for non-<see cref="Models.HealthBand.Neutral"/> values.
/// </param>
/// <param name="DrillHref">
///     Optional anchor URL for the per-row drill action. The action cell renders
///     a "View" link only when this is set.
/// </param>
/// <param name="Description">
///     Optional long-form description shown as the <c>title</c> attribute on the
///     name cell -- typically the meter's docstring from <c>SignalCatalog</c>.
/// </param>
public sealed record MeterTableRowViewModel(
    string Name,
    string Kind,
    string Value,
    string? Unit = null,
    SparklineViewModel? Sparkline = null,
    HealthBand HealthBand = HealthBand.Neutral,
    string? DrillHref = null,
    string? Description = null,
    string? Label = null);
