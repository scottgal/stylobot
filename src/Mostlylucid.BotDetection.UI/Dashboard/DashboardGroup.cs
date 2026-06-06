namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     A static group of nav rows (e.g. LIVE, INVESTIGATION, POLICY, SYSTEM).
///     Groups are FOSS-owned and not extensible from commercial -- packs
///     contribute via <see cref="IDashboardPack" /> into the PACKS group only.
/// </summary>
public sealed record DashboardGroup(
    string Id,
    string Label,
    IReadOnlyList<DashboardRow> Rows);
