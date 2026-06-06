namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     A single nav row inside a <see cref="DashboardGroup" />.
/// </summary>
/// <param name="Id">Path-safe slug used as the URL segment.</param>
/// <param name="Label">Display label in the nav.</param>
/// <param name="PartialPath">Razor partial path to render when this row is active.</param>
/// <param name="IsCommercialOnly">
///     When true, the row only renders when
///     <c>DashboardShellModel.IsCommercial</c> is true. The route handler still
///     accepts the segment; the partial returns its own empty / 402 state.
/// </param>
/// <param name="IsHidden">
///     When true, the row dispatches as a route but does NOT render in the nav.
///     Used for legacy routes (countries, identities, etc.) kept for deep-link
///     compatibility after the spec dropped them from the nav.
/// </param>
public sealed record DashboardRow(
    string Id,
    string Label,
    string PartialPath,
    bool IsCommercialOnly = false,
    bool IsHidden = false);
