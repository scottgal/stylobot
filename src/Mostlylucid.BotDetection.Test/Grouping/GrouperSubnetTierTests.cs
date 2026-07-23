using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Grouping;

namespace Mostlylucid.BotDetection.Test.Grouping;

/// <summary>
///     Wires the real <see cref="SubnetRotationTracker"/> into the grouper
///     and asserts that tier 5 (subnet rotation) fires after three bot
///     observations from the same /24 with three UA families.
/// </summary>
public class GrouperSubnetTierTests
{
    private static (BehaviouralGrouper grouper, SubnetRotationTracker tracker) Build(
        GroupingOptions? options = null)
    {
        options ??= new GroupingOptions();
        var tracker = new SubnetRotationTracker();
        var grouper = new BehaviouralGrouper(
            new TestMonitor<GroupingOptions>(options),
            NullLogger<BehaviouralGrouper>.Instance,
            tracker);
        return (grouper, tracker);
    }

    private static GroupingInput Bot(
        string signature,
        string subnet,
        string uaFamily,
        string country = "US",
        string? botName = null,
        string? botType = null) =>
        new()
        {
            Signature = signature,
            BotProbability = 0.92,
            RiskBand = "High",
            IsBot = true,
            BotName = botName,
            BotType = botType,
            IpSubnetSignature = subnet,
            UaFamily = uaFamily,
            CountryCode = country
        };

    [Fact]
    public void ThreeBotsSameSubnetDifferentUaFamilies_CollapseAsSubnetRotation()
    {
        var (grouper, _) = Build();

        // First two are below threshold -> fall through to RawSignature.
        var k1 = grouper.Resolve(Bot("sig:1", "net:abc", "curl"));
        var k2 = grouper.Resolve(Bot("sig:2", "net:abc", "Python"));
        Assert.Equal(GroupKeySource.RawSignature, k1.Source);
        Assert.Equal(GroupKeySource.RawSignature, k2.Source);

        // Third observation crosses the threshold -> tier 5 fires.
        var k3 = grouper.Resolve(Bot("sig:3", "net:abc", "Chrome"));
        Assert.Equal(GroupKeySource.SubnetUaRotation, k3.Source);
        Assert.Equal("net:abc", k3.Value);
        Assert.Contains("3 sigs", k3.Display);
        Assert.Contains("3 UAs", k3.Display);
    }

    [Fact]
    public void FiveBotsSameUaFamily_FallThrough_RealNAT()
    {
        var (grouper, _) = Build();
        for (var i = 1; i <= 5; i++)
        {
            var key = grouper.Resolve(Bot($"sig:{i}", "net:nat", "Chrome"));
            // No UA diversity -> never reaches rotation tier; falls through
            // to friendly name or raw.
            Assert.NotEqual(GroupKeySource.SubnetUaRotation, key.Source);
        }
    }

    [Fact]
    public void SubnetRotation_WinsOverFriendlyName()
    {
        var (grouper, _) = Build();
        // All three rows ARE Amazonbot (tier 6 candidate) but they're also
        // hitting from a single /24 with rotating UAs -- subnet rotation
        // (tier 5) should win because it's a stronger behavioural group.
        grouper.Resolve(Bot("sig:1", "net:abc", "curl", botName: "Amazonbot", botType: "Scraper"));
        grouper.Resolve(Bot("sig:2", "net:abc", "Python", botName: "Amazonbot", botType: "Scraper"));
        var k = grouper.Resolve(Bot("sig:3", "net:abc", "Go-http", botName: "Amazonbot", botType: "Scraper"));

        Assert.Equal(GroupKeySource.SubnetUaRotation, k.Source);
    }

    [Fact]
    public void IdentityStillBeatsSubnetRotation()
    {
        var (grouper, _) = Build();
        // Seed tracker to threshold first, then observe a request with a
        // FingerprintId -- identity wins regardless.
        grouper.Resolve(Bot("sig:1", "net:abc", "curl"));
        grouper.Resolve(Bot("sig:2", "net:abc", "Python"));
        var input = Bot("sig:3", "net:abc", "Chrome") with { FingerprintId = "fp_x" };
        var k = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.Identity, k.Source);
    }

    [Fact]
    public void DisabledTier_FallsThrough()
    {
        var (grouper, _) = Build(new GroupingOptions { EnableSubnetRotationTier = false });
        grouper.Resolve(Bot("sig:1", "net:abc", "curl"));
        grouper.Resolve(Bot("sig:2", "net:abc", "Python"));
        var k = grouper.Resolve(Bot("sig:3", "net:abc", "Chrome"));

        Assert.NotEqual(GroupKeySource.SubnetUaRotation, k.Source);
    }

    [Fact]
    public void HumanInRotatingSubnet_StillReturnsHuman_NotCollapsed()
    {
        // Critical invariant: even if three bots from /24 ABC formed a
        // rotation group, a human from /24 ABC must NOT be grouped with
        // them.
        var (grouper, _) = Build();
        grouper.Resolve(Bot("sig:bot1", "net:abc", "curl"));
        grouper.Resolve(Bot("sig:bot2", "net:abc", "Python"));
        grouper.Resolve(Bot("sig:bot3", "net:abc", "Chrome"));

        var human = new GroupingInput
        {
            Signature = "sig:human",
            BotProbability = 0.05,
            RiskBand = "VeryLow",
            IsBot = false,
            IpSubnetSignature = "net:abc",
            UaFamily = "Safari",
            CountryCode = "US"
        };
        var k = grouper.Resolve(human);

        Assert.Equal(GroupKeySource.Human, k.Source);
        Assert.Equal("sig:human", k.Value);
    }

    private sealed class TestMonitor<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }
}
