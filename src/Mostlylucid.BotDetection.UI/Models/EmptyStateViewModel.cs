namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Shared shape for the dashboard's "nothing here yet" card (icon + heading + one-line
///     detail, optional CTA) -- extracted from the pattern several widgets (Timeline, Service
///     map, Log sink's "not configured" state) already hand-duplicated identically. New
///     empty/unavailable states should use this instead of a bare unstyled paragraph.
/// </summary>
/// <param name="Icon">A Boxicons class suffix, e.g. "time-five" for <c>bx-time-five</c>.</param>
/// <param name="Heading">Short title, e.g. "No timeline observations".</param>
/// <param name="Detail">One-line explanation of why it's empty and/or what would fill it.</param>
/// <param name="CtaHref">Optional link target, e.g. "/store/domains".</param>
/// <param name="CtaLabel">Optional link text, shown appended after <paramref name="Detail"/>.</param>
/// <param name="Compact">
///     True when this renders inside a widget that already has its own card/border (e.g. Data
///     guardians, the Public Key registry) -- drops the outer card so it doesn't nest card-in-card.
/// </param>
public sealed record EmptyStateViewModel(
    string Icon,
    string Heading,
    string Detail,
    string? CtaHref = null,
    string? CtaLabel = null,
    bool Compact = false);
