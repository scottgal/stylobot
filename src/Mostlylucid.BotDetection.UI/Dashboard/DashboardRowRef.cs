namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     A reference to whichever row is currently active. <c>Area</c> is the
///     top-level segment (group row id OR pack id). <c>Sub</c> is non-null only
///     when the active row is a pack sub-row.
/// </summary>
public sealed record DashboardRowRef(string Area, string? Sub = null)
{
    /// <summary>Default landing row when the operator hits /stylobot bare.</summary>
    public static DashboardRowRef Default { get; } = new("overview");
}
