namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     View model for <c>SbHealthDot</c> -- a single coloured dot bound to a
///     <see cref="HealthBand"/>. Used standalone (pack-hub headers, tile rows) and
///     as the left-border driver inside <c>SbStatTile</c>.
/// </summary>
/// <param name="Band">Canonical health band; drives the dot colour class.</param>
/// <param name="Tooltip">
///     Free-form description shown via the <c>title</c> attribute and used as the
///     screen-reader <c>aria-label</c>. When empty, the band name itself is the
///     fallback aria label so screen readers never report a meaningless dot.
/// </param>
public sealed record HealthDotViewModel(HealthBand Band, string Tooltip);
