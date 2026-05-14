using Mostlylucid.BotDetection.MonitoringPacks;

namespace Mostlylucid.BotDetection.Test.MonitoringPacks;

public class PackRuntimeControllerTests
{
    [Fact]
    public void NullController_SupportsHotReload_AlwaysFalse()
    {
        var c = new NullPackRuntimeController();
        Assert.False(c.SupportsHotReload("aspnet-monitoring"));
        Assert.False(c.SupportsHotReload("anything"));
        Assert.False(c.SupportsHotReload(""));
    }

    [Fact]
    public async Task NullController_ReplacePack_Throws()
    {
        var c = new NullPackRuntimeController();
        var pack = new TestPack(Array.Empty<MeterCollectionGroup>());
        await Assert.ThrowsAsync<NotSupportedException>(
            () => c.ReplacePackAsync(pack, CancellationToken.None));
    }

    [Fact]
    public async Task NullController_ReloadAll_NoOp()
    {
        var c = new NullPackRuntimeController();
        await c.ReloadAllAsync(CancellationToken.None);
    }

    [Fact]
    public void PackChangedEventArgs_CarriesPack()
    {
        var pack = new TestPack(Array.Empty<MeterCollectionGroup>(), id: "carried-pack");
        var args = new PackChangedEventArgs(pack);
        Assert.Equal("carried-pack", args.Pack.Id);
    }

    [Fact]
    public void NullController_PackChanged_NeverFires()
    {
        var c = new NullPackRuntimeController();
        var fired = false;
        c.PackChanged += (_, _) => fired = true;
        Assert.False(fired);
    }
}
