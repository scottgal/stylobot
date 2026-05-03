using Mostlylucid.BotDetection.Llm.Holodeck;
using Mostlylucid.BotDetection.SimulationPacks;

namespace Mostlylucid.BotDetection.Test.Holodeck;

public class HolodeckResponseCacheTests
{
    [Fact]
    public void TryGet_Miss_ReturnsFalse()
    {
        var cache = new HolodeckResponseCache(maxSize: 10, ttl: TimeSpan.FromHours(1));
        Assert.False(cache.TryGet("fp1", "/path", out _));
    }

    [Fact]
    public void Set_ThenGet_ReturnsValue()
    {
        var cache = new HolodeckResponseCache(maxSize: 10, ttl: TimeSpan.FromHours(1));
        var response = new HolodeckResponse { Content = "<html>fake</html>", ContentType = "text/html", WasGenerated = true };
        cache.Set("fp1", "/wp-login.php", response);
        Assert.True(cache.TryGet("fp1", "/wp-login.php", out var cached));
        Assert.Equal("<html>fake</html>", cached!.Content);
    }

    [Fact]
    public void DifferentFingerprints_DifferentEntries()
    {
        var cache = new HolodeckResponseCache(maxSize: 10, ttl: TimeSpan.FromHours(1));
        var r1 = new HolodeckResponse { Content = "resp1", ContentType = "text/html", WasGenerated = true };
        var r2 = new HolodeckResponse { Content = "resp2", ContentType = "text/html", WasGenerated = true };
        cache.Set("fp1", "/path", r1);
        cache.Set("fp2", "/path", r2);
        cache.TryGet("fp1", "/path", out var got1);
        cache.TryGet("fp2", "/path", out var got2);
        Assert.Equal("resp1", got1!.Content);
        Assert.Equal("resp2", got2!.Content);
    }

    [Fact]
    public void MaxSize_EvictsOldest()
    {
        var cache = new HolodeckResponseCache(maxSize: 2, ttl: TimeSpan.FromHours(1));
        var r = new HolodeckResponse { Content = "x", ContentType = "text/html", WasGenerated = true };
        cache.Set("fp1", "/a", r);
        cache.Set("fp2", "/b", r);
        cache.Set("fp3", "/c", r);
        Assert.False(cache.TryGet("fp1", "/a", out _));
        Assert.True(cache.TryGet("fp2", "/b", out _));
        Assert.True(cache.TryGet("fp3", "/c", out _));
    }

    [Fact]
    public void Count_TracksEntries()
    {
        var cache = new HolodeckResponseCache(maxSize: 10, ttl: TimeSpan.FromHours(1));
        var r = new HolodeckResponse { Content = "x", ContentType = "text/html", WasGenerated = true };
        Assert.Equal(0, cache.Count);
        cache.Set("fp1", "/a", r);
        Assert.Equal(1, cache.Count);
    }
}
