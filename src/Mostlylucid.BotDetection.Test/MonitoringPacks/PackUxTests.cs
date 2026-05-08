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
    public void MonitoringPackOptions_Enabled_DefaultsToTrue()
    {
        var opts = new MonitoringPackOptions();
        Assert.True(opts.Enabled);
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
            ActiveTab     = "overview",
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
            License       = null!,
            MonitoringPacks = packs,
        };
}
