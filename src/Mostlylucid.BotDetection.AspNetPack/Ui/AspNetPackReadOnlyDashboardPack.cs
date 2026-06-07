using Mostlylucid.BotDetection.UI.Dashboard;

namespace Mostlylucid.BotDetection.AspNetPack.Ui;

/// <summary>
///     FOSS read-only contribution to the dashboard left-nav. Declares the
///     two sub-rows that depend purely on in-process data the host already
///     exposes (the routing table + the current request principal).
/// </summary>
/// <remarks>
///     The commercial <c>AspNetPackDashboardPack</c> in <c>stylobot-commercial</c>
///     contributes additional sub-rows (Auth events, Log sink) on top of these.
///     Both packs use the same <see cref="IDashboardPack.Id" /> -- last-write-wins
///     in the <see cref="IEnumerable{IDashboardPack}" /> ordering for v1; a structured
///     merge contract is a follow-up.
/// </remarks>
public sealed class AspNetPackReadOnlyDashboardPack : IDashboardPack
{
    public string Id => "aspnet-pack";
    public string Label => "ASP.NET Pack";
    public string Icon => "bx bx-server";

    public IReadOnlyList<DashboardSubRow> SubRows { get; } =
    [
        new DashboardSubRow("routes",   "Routes",   "SbAspNetEndpoints"),
        new DashboardSubRow("identity", "Identity", "SbAspNetIdentity"),
    ];
}