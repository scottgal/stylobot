namespace Mostlylucid.BotDetection.UI.Dashboard;

/// <summary>
///     Dashboard contribution contract for packs. A pack typically implements
///     this AND <see cref="Mostlylucid.BotDetection.MonitoringPacks.IMonitoringPack" />:
///     the latter declares "here is the telemetry I emit"; this one declares
///     "here is the dashboard surface I contribute". The two are orthogonal --
///     packs may implement one, the other, or both.
/// </summary>
public interface IDashboardPack
{
    /// <summary>Path-safe slug used as the URL segment (e.g. "aspnet-pack").</summary>
    string Id { get; }

    /// <summary>Display label shown as the pack header in the nav (e.g. "ASP.NET Pack").</summary>
    string Label { get; }

    /// <summary>Boxicons class for the pack header icon (e.g. "bx bx-server").</summary>
    string Icon { get; }

    /// <summary>Sub-rows the pack contributes under its header.</summary>
    IReadOnlyList<DashboardSubRow> SubRows { get; }
}
