using Mostlylucid.BotDetection.Packs;

namespace Mostlylucid.BotDetection.Test.Packs;

public class PackRegistryTests
{
    [Fact]
    public void Add_ItemAppearsInEnumeration()
    {
        var registry = new PackRegistry<string>();
        registry.Add("hello");
        Assert.Contains("hello", registry);
    }

    [Fact]
    public void EmptyRegistry_EnumeratesEmpty()
    {
        var registry = new PackRegistry<string>();
        Assert.Empty(registry);
    }

    [Fact]
    public void Clear_RemovesAllItems()
    {
        var registry = new PackRegistry<string>();
        registry.Add("a");
        registry.Add("b");
        registry.Clear();
        Assert.Empty(registry);
    }

    [Fact]
    public void MultipleItems_AllEnumerated()
    {
        var registry = new PackRegistry<int>();
        registry.Add(1);
        registry.Add(2);
        registry.Add(3);
        Assert.Equal(3, registry.Count());
    }
}
