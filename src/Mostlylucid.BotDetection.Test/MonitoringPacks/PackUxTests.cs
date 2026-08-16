using Mostlylucid.BotDetection.MonitoringPacks;
using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class PackUxTests
{
    [Fact]
    public void AspNetMonitoringPack_TabName_IsSystem()
    {
        var pack = new AspNetMonitoringPack();
        Assert.Equal("System", pack.TabName);
    }

    [Fact]
    public void MonitoringPackOptions_Enabled_DefaultsToFalse()
    {
        // The base FOSS binary ships the monitoring pack disabled so operators
        // opt in explicitly. Commercial variant binaries flip this to true.
        var opts = new MonitoringPackOptions();
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void DashboardShellModel_MonitoringPacks_DefaultsToEmpty()
    {
        var model = BuildShellModel([]);
        Assert.Empty(model.MonitoringPacks);
        Assert.False(model.HasPackTabs);
    }

    [Fact]
    public void DashboardShellModel_HasPackTabs_TrueWhenListNotEmpty()
    {
        var model = BuildShellModel([new PackTabInfo("aspnet-monitoring", "System")]);
        Assert.True(model.HasPackTabs);
    }

    [Fact]
    public void DashboardShellModel_IsPackTab_ReturnsTrueForRegisteredId()
    {
        var model = BuildShellModel([new PackTabInfo("aspnet-monitoring", "System")]);
        Assert.True(model.IsPackTab("aspnet-monitoring"));
        Assert.False(model.IsPackTab("metrics"));
        Assert.False(model.IsPackTab("overview"));
    }

    private static DashboardShellModel BuildShellModel(IReadOnlyList<PackTabInfo> packs) =>
        new()
        {
            CspNonce      = "test",
            BasePath      = "/stylobot",
            HubPath       = "/stylobot/hub",
            ActiveRow     = Mostlylucid.BotDetection.UI.Dashboard.DashboardRowRef.Default,
            Summary       = null!,
            Visitors      = null!,
            YourDetection = null!,
            Countries     = null!,
            Endpoints     = null!,
            Clusters      = null!,
            UserAgents    = null!,
            TopBots       = null!,
            Sessions      = null!,
            Threats       = null!,
            Packs         = packs.Select(p => (Mostlylucid.BotDetection.UI.Dashboard.IDashboardPack)
                new ShimPack(p.Id, p.TabName)).ToList(),
        };

    private sealed record ShimPack(string Id, string Label) : Mostlylucid.BotDetection.UI.Dashboard.IDashboardPack
    {
        public string Icon => "bx bx-cube";
        public IReadOnlyList<Mostlylucid.BotDetection.UI.Dashboard.DashboardSubRow> SubRows =>
            Array.Empty<Mostlylucid.BotDetection.UI.Dashboard.DashboardSubRow>();
    }
}
