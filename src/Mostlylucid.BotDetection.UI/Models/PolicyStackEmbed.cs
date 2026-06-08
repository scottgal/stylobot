namespace Mostlylucid.BotDetection.UI.Models;

/// <summary>
///     Which surface shape the <c>SbPolicyStack</c> view component should render.
///     Pre-baked into the view-model so the Razor partials can dispatch with a
///     <c>switch</c> instead of branching on caller-side flags.
/// </summary>
public enum PolicyStackEmbed
{
    /// <summary>Full embed: breadcrumb header, Effective + Stack tabs, rule rows.</summary>
    Full,

    /// <summary>Just the rule list -- no breadcrumb, no tabs. For tight panes.</summary>
    EffectiveOnly,

    /// <summary>One-line "N rules effective here -- last edit X ago" badge.</summary>
    StatusBadge
}
