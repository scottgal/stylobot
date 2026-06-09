namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Canonical health bands used by every cohesive-views widget that shows a
///     single-glance status colour. Mapped to dashboard colour semantics per
///     <c>project_dashboard_color_semantics</c>:
///     <list type="bullet">
///         <item><description><see cref="Good"/> -> success green (healthy / human / pass)</description></item>
///         <item><description><see cref="Caution"/> -> warning yellow (degraded / elevated)</description></item>
///         <item><description><see cref="Warning"/> -> warning yellow (alias of <see cref="Caution"/>; kept so a future divergence -- say, distinct caution vs. warning thresholds -- is a CSS-only change, not an enum churn)</description></item>
///         <item><description><see cref="Danger"/> -> error red (failing / bot / breach)</description></item>
///         <item><description><see cref="Neutral"/> -> muted text (unknown / not configured)</description></item>
///     </list>
/// </summary>
public enum HealthBand
{
    Good,
    Caution,
    Warning,
    Danger,
    Neutral
}
