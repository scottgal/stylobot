using Mostlylucid.BotDetection.Grouping;

namespace Mostlylucid.BotDetection.Test.Grouping;

public class SubnetRotationTrackerTests
{
    [Fact]
    public void BelowSignatureThreshold_ReturnsNull()
    {
        var tracker = new SubnetRotationTracker();
        Assert.Null(tracker.RecordAndCheck("net:abc", "sig:1", "curl", "US", minSignatures: 3, minUaFamilies: 3));
        Assert.Null(tracker.RecordAndCheck("net:abc", "sig:2", "Python", "US", minSignatures: 3, minUaFamilies: 3));
    }

    [Fact]
    public void EnoughSigsAndUaFamilies_ReturnsGroup()
    {
        var tracker = new SubnetRotationTracker();
        tracker.RecordAndCheck("net:abc", "sig:1", "curl", "US", 3, 3);
        tracker.RecordAndCheck("net:abc", "sig:2", "Python", "US", 3, 3);
        var group = tracker.RecordAndCheck("net:abc", "sig:3", "Chrome", "US", 3, 3);

        Assert.NotNull(group);
        Assert.Equal(3, group!.SignatureCount);
        Assert.Equal(3, group.UaFamilyCount);
        Assert.Equal("US", group.Country);
    }

    [Fact]
    public void EnoughSigsButOneUaFamily_FallsThrough_RealNAT()
    {
        // 5 signatures all running Chrome on Windows -> looks like corporate
        // NAT, not an actor rotating UAs. Should NOT group.
        var tracker = new SubnetRotationTracker();
        for (var i = 1; i <= 5; i++)
        {
            var result = tracker.RecordAndCheck("net:abc", $"sig:{i}", "Chrome", "US", 3, 3);
            Assert.Null(result);
        }
    }

    [Fact]
    public void MultiCountry_FallsThrough_DifferentActorsOnAProxyPool()
    {
        // Same /24 but distinct countries means the signatures are
        // hitting through a proxy / VPN pool -- they're DIFFERENT actors
        // sharing exit IPs, not a single rotating actor.
        var tracker = new SubnetRotationTracker();
        tracker.RecordAndCheck("net:abc", "sig:1", "curl", "US", 3, 3);
        tracker.RecordAndCheck("net:abc", "sig:2", "Python", "GB", 3, 3);
        var result = tracker.RecordAndCheck("net:abc", "sig:3", "Chrome", "DE", 3, 3);

        Assert.Null(result);
    }

    [Fact]
    public void DifferentSubnets_HaveOwnBudgets()
    {
        var tracker = new SubnetRotationTracker();
        for (var i = 1; i <= 3; i++)
            tracker.RecordAndCheck("net:abc", $"a:{i}", $"UA{i}", "US", 3, 3);

        // Fresh subnet starts at zero.
        var group = tracker.RecordAndCheck("net:def", "b:1", "curl", "US", 3, 3);
        Assert.Null(group);
    }

    [Fact]
    public void SameSignatureRepeated_DoesNotGrow()
    {
        var tracker = new SubnetRotationTracker();
        // Same signature 10 times -> still just one signature on the window.
        for (var i = 0; i < 10; i++)
            tracker.RecordAndCheck("net:abc", "sig:1", "curl", "US", 3, 3);

        var snap = tracker.Peek("net:abc");
        Assert.Equal(1, snap?.SignatureCount);
    }

    [Fact]
    public void ZeroMinSignatures_DisablesTier()
    {
        var tracker = new SubnetRotationTracker();
        var result = tracker.RecordAndCheck("net:abc", "sig:1", "curl", "US", 0, 3);
        Assert.Null(result);
    }

    [Fact]
    public void Peek_UnknownSubnet_ReturnsNull()
    {
        var tracker = new SubnetRotationTracker();
        Assert.Null(tracker.Peek("nobody"));
    }
}
