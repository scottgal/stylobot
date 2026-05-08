using Mostlylucid.BotDetection.Metrics;
using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class AspNetMonitoringPackTests
{
    [Fact]
    public void Pack_Id_IsStable()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: false);
        Assert.Equal("aspnet-monitoring", pack.Id);
    }

    [Fact]
    public void Pack_DefaultMode_ContainsStyloBottMeter()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: false);
        Assert.Contains(pack.MeterGroups, g => g.MeterName == BotDetectionMetrics.MeterName);
    }

    [Fact]
    public void Pack_DefaultMode_ContainsAllStylosBotInstruments()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: false);
        var sbGroup = pack.MeterGroups.Single(g => g.MeterName == BotDetectionMetrics.MeterName);
        var instruments = sbGroup.Instruments.Select(i => i.InstrumentName).ToHashSet();
        Assert.Contains("botdetection.requests.total", instruments);
        Assert.Contains("botdetection.bots.detected", instruments);
        Assert.Contains("botdetection.humans.detected", instruments);
        Assert.Contains("botdetection.detection.duration", instruments);
        Assert.Contains("botdetection.confidence.average", instruments);
        Assert.Contains("botdetection.errors.total", instruments);
        Assert.Contains("botdetection.weightstore.cache.hits", instruments);
        Assert.Contains("botdetection.weightstore.cache.misses", instruments);
    }

    [Fact]
    public void Pack_HostMetersEnabled_ContainsAspNetMeter()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: true);
        Assert.Contains(pack.MeterGroups, g => g.MeterName == "Microsoft.AspNetCore.Hosting");
    }

    [Fact]
    public void Pack_HostMetersEnabled_ContainsRuntimeMeter()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: true);
        Assert.Contains(pack.MeterGroups, g => g.MeterName == "System.Runtime");
    }

    [Fact]
    public void Pack_CollectionInterval_IsOneMinute()
    {
        var pack = new AspNetMonitoringPack(includeHostMeters: false);
        Assert.Equal(TimeSpan.FromSeconds(60), pack.CollectionInterval);
    }
}
