using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

internal sealed class TestPack(
    IReadOnlyList<MeterCollectionGroup> groups,
    string id = "test",
    string tabName = "Test")
    : IMonitoringPack
{
    public string Id => id;
    public string Name => "Test Pack";
    public string Description => "Test";
    public string TabName => tabName;
    public TimeSpan CollectionInterval => TimeSpan.FromSeconds(60);
    public IReadOnlyList<MeterCollectionGroup> MeterGroups => groups;
}
