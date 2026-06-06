using Mostlylucid.BotDetection.UI.Dashboard;

namespace Mostlylucid.BotDetection.Test.Dashboard;

/// <summary>
///     Deterministic <see cref="IDashboardPack" /> used by route tests that
///     need to assert pack sub-rows render. Two sub-rows so we can verify the
///     "bare pack id redirects to first sub-row" behaviour.
/// </summary>
public sealed class StubDashboardPack : IDashboardPack
{
    public string Id => "stub-pack";
    public string Label => "Stub Pack";
    public string Icon => "bx bx-cube";

    public IReadOnlyList<DashboardSubRow> SubRows { get; } =
    [
        new DashboardSubRow("alpha", "Alpha", "StubAlpha"),
        new DashboardSubRow("beta",  "Beta",  "StubBeta"),
    ];
}
