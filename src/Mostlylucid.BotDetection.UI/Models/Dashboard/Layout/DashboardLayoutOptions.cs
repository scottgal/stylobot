namespace Mostlylucid.BotDetection.UI.Models.Dashboard.Layout;

public sealed class DashboardLayoutOptions
{
    /// <summary>How many rows each breakdown card shows on Traffic.</summary>
    public int TrafficCardTopN { get; set; } = 8;

    /// <summary>Default chart window on Traffic in minutes; URL ?window= overrides.</summary>
    public int DefaultTimeWindowMinutes { get; set; } = 60;

    /// <summary>Header search type-ahead result cap.</summary>
    public int SearchMaxResults { get; set; } = 10;

    /// <summary>
    ///     Kill-switch during migration. When true (default after M1), sidebar +
    ///     landing-page routing use the new IA (Traffic default, three aggregates
    ///     + packs + manage). When false, the legacy 10+ tab sidebar continues.
    ///     URL ?legacy=1 forces legacy even when this is true. Removed in M2 after
    ///     legacy surfaces are deleted.
    /// </summary>
    public bool V2Enabled { get; set; } = true;   // was false; M1 flips to default-on
}
