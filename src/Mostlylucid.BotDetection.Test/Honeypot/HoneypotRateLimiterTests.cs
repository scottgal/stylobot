using Mostlylucid.BotDetection.Honeypot;

namespace Mostlylucid.BotDetection.Test.Honeypot;

public class HoneypotRateLimiterTests
{
    [Fact]
    public void UnderCap_AllowsHits()
    {
        var limiter = new HoneypotRateLimiter();
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.RecordAndCheck("sig:abc", maxPerMinute: 10, maxPerHour: 60));
    }

    [Fact]
    public void OverPerMinute_Caps()
    {
        var limiter = new HoneypotRateLimiter();
        for (var i = 0; i < 10; i++)
            Assert.True(limiter.RecordAndCheck("sig:abc", maxPerMinute: 10, maxPerHour: 60));
        Assert.False(limiter.RecordAndCheck("sig:abc", maxPerMinute: 10, maxPerHour: 60));
    }

    [Fact]
    public void DifferentKey_HasOwnBudget()
    {
        var limiter = new HoneypotRateLimiter();
        for (var i = 0; i < 10; i++)
            Assert.True(limiter.RecordAndCheck("sig:a", maxPerMinute: 10, maxPerHour: 60));
        Assert.False(limiter.RecordAndCheck("sig:a", maxPerMinute: 10, maxPerHour: 60));
        // Different key has fresh budget.
        Assert.True(limiter.RecordAndCheck("sig:b", maxPerMinute: 10, maxPerHour: 60));
    }

    [Fact]
    public void ZeroLimits_DisablesCheck()
    {
        var limiter = new HoneypotRateLimiter();
        for (var i = 0; i < 1000; i++)
            Assert.True(limiter.RecordAndCheck("sig:abc", maxPerMinute: 0, maxPerHour: 0));
    }

    [Fact]
    public void Snapshot_ReturnsCounts()
    {
        var limiter = new HoneypotRateLimiter();
        for (var i = 0; i < 3; i++)
            limiter.RecordAndCheck("sig:abc", maxPerMinute: 100, maxPerHour: 100);

        var (minute, hour) = limiter.Snapshot("sig:abc");
        Assert.Equal(3, minute);
        Assert.Equal(3, hour);
    }

    [Fact]
    public void Snapshot_UnknownKey_ReturnsZeros()
    {
        var limiter = new HoneypotRateLimiter();
        var (minute, hour) = limiter.Snapshot("nobody");
        Assert.Equal(0, minute);
        Assert.Equal(0, hour);
    }
}
