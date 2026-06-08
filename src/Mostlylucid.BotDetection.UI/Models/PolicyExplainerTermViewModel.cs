namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     One term row in the explainer panel's decision trace. Pre-rendered by
///     <see cref="Services.PolicyExplainerPresenter"/> so the Razor partial
///     stays declarative -- no service calls or formatting decisions live in
///     the template.
/// </summary>
/// <param name="Description">Pre-rendered term shorthand -- <c>bot.type = 'scraper'</c>, <c>score.bot_probability &gt;= 0.7</c>.</param>
/// <param name="Tooltip">Catalog short description for the facet. Empty when the catalog has no entry.</param>
/// <param name="Matched"><c>true</c> when the term evaluated true for the replayed cycle.</param>
/// <param name="Indicator">Glyph the partial renders (✓ for matched, ✗ for missed). Pre-baked so the template doesn't branch.</param>
/// <param name="Detail">Per-term detail line -- e.g. <c>actual = scraper; expected one of (scraper, crawler)</c>, or the short-circuit reason for skipped siblings.</param>
/// <param name="EvalMicros">Per-term lookup + compare cost in microseconds. Zero for short-circuited siblings.</param>
public sealed record PolicyExplainerTermViewModel(
    string Description,
    string Tooltip,
    bool Matched,
    string Indicator,
    string Detail,
    long EvalMicros);
