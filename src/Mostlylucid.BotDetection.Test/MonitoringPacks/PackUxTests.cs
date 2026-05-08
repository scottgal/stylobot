using Mostlylucid.BotDetection.UI.Configuration;
using Mostlylucid.BotDetection.UI.Models;
using Xunit;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class PackUxTests
{
    [Fact]
    public void MonitoringPackOptions_Enabled_DefaultsToFalse()
    {
        var opts = new MonitoringPackOptions();
        Assert.False(opts.Enabled);
    }

    [Fact]
    public void MonitoringPackOptions_CanSetEnabled()
    {
        var opts = new MonitoringPackOptions { Enabled = true };
        Assert.True(opts.Enabled);
    }

    [Fact]
    public void DashboardShellModel_MonitoringPackEnabled_CanBeFalse()
    {
        var model = BuildMinimalShellModel(monitoringPackEnabled: false);
        Assert.False(model.MonitoringPackEnabled);
    }

    [Fact]
    public void DashboardShellModel_MonitoringPackEnabled_CanBeTrue()
    {
        var model = BuildMinimalShellModel(monitoringPackEnabled: true);
        Assert.True(model.MonitoringPackEnabled);
    }

    private static DashboardShellModel BuildMinimalShellModel(bool monitoringPackEnabled) =>
        new()
        {
            CspNonce = "test",
            BasePath = "/stylobot",
            HubPath = "/stylobot/hub",
            ActiveTab = "overview",
            Summary = null!,
            Visitors = null!,
            YourDetection = null!,
            Countries = null!,
            Endpoints = null!,
            Clusters = null!,
            UserAgents = null!,
            TopBots = null!,
            Sessions = null!,
            Threats = null!,
            License = null!,
            MonitoringPackEnabled = monitoringPackEnabled
        };
}
