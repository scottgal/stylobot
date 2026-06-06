namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Composed view over <see cref="FossDashboardGroups" /> + the
///     <see cref="IDashboardPack" /> instances registered in DI. The middleware
///     consults this single surface to resolve a (<c>area</c>, <c>sub</c>) tuple
///     into either a Razor partial path or a view component invocation.
/// </summary>
public interface IDashboardRowRegistry
{
    /// <summary>FOSS groups in render order (LIVE, INVESTIGATION, POLICY, SYSTEM).</summary>
    IReadOnlyList<DashboardGroup> Groups { get; }

    /// <summary>Registered packs in DI registration order.</summary>
    IReadOnlyList<IDashboardPack> Packs { get; }

    /// <summary>
    ///     Resolve a (<c>area</c>, <c>sub</c>) tuple. Returns null when the
    ///     tuple does not match any row.
    /// </summary>
    DashboardRowMatch? Resolve(string area, string? sub);
}

/// <summary>Resolved row + its dispatch target.</summary>
/// <param name="Ref">The matched row reference.</param>
/// <param name="PartialPath">Razor partial path when the match is a group row; null for pack sub-rows.</param>
/// <param name="ViewComponentName">View component name when the match is a pack sub-row; null for group rows.</param>
/// <param name="Pack">Owning pack when the match is a pack sub-row; null for group rows.</param>
/// <param name="IsCommercialOnly">Group-row commercial gating flag (always false for pack sub-rows).</param>
public sealed record DashboardRowMatch(
    DashboardRowRef Ref,
    string? PartialPath,
    string? ViewComponentName,
    IDashboardPack? Pack,
    bool IsCommercialOnly);
