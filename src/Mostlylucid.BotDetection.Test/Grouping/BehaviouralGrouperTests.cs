using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Mostlylucid.BotDetection.Grouping;

namespace Mostlylucid.BotDetection.Test.Grouping;

public class BehaviouralGrouperTests
{
    private static BehaviouralGrouper Build(GroupingOptions? options = null)
    {
        options ??= new GroupingOptions();
        return new BehaviouralGrouper(
            new TestMonitor<GroupingOptions>(options),
            NullLogger<BehaviouralGrouper>.Instance,
            new SubnetRotationTracker());
    }

    private static GroupingInput Bot(string signature, string? botName = null, string? botType = null) =>
        new()
        {
            Signature = signature,
            BotProbability = 0.92,
            RiskBand = "High",
            IsBot = true,
            BotName = botName,
            BotType = botType
        };

    private static GroupingInput Human(string signature) =>
        new()
        {
            Signature = signature,
            BotProbability = 0.05,
            RiskBand = "VeryLow",
            IsBot = false
        };

    // ============================================================
    //  Tier 0: bot-only gate (the non-overridable invariant)
    // ============================================================

    [Fact]
    public void Human_AlwaysReturnsHumanSource_NeverGrouped()
    {
        var grouper = Build();
        var key = grouper.Resolve(Human("sig:human-a"));

        Assert.Equal(GroupKeySource.Human, key.Source);
        Assert.Equal("sig:human-a", key.Value);
    }

    [Fact]
    public void TwoHumans_GetDistinctGroupKeys_NeverShare()
    {
        var grouper = Build();
        var keyA = grouper.Resolve(Human("sig:a"));
        var keyB = grouper.Resolve(Human("sig:b"));

        // Both are Human source, but the Value field is the signature so
        // they remain DIFFERENT keys -- they will not collapse into one row.
        Assert.Equal(GroupKeySource.Human, keyA.Source);
        Assert.Equal(GroupKeySource.Human, keyB.Source);
        Assert.NotEqual(keyA.Canonical, keyB.Canonical);
    }

    [Fact]
    public void TwoHumansSharingABotName_StillDoNotGroup()
    {
        // Hostile-but-not-quite case: a row with BotName populated but
        // BotProbability low. Bot-only gate must override the friendly
        // tier and treat as human.
        var grouper = Build();
        var a = new GroupingInput
        {
            Signature = "sig:a",
            BotProbability = 0.10,
            RiskBand = "Low",
            IsBot = false,
            BotName = "Amazonbot",
            BotType = "Scraper"
        };
        var b = a with { Signature = "sig:b" };

        var keyA = grouper.Resolve(a);
        var keyB = grouper.Resolve(b);

        Assert.Equal(GroupKeySource.Human, keyA.Source);
        Assert.Equal(GroupKeySource.Human, keyB.Source);
        Assert.NotEqual(keyA.Canonical, keyB.Canonical);
    }

    [Fact]
    public void BotBelowProbabilityThreshold_FallsThroughToHuman()
    {
        var grouper = Build();
        var input = Bot("sig:x", "Amazonbot", "Scraper") with { BotProbability = 0.40 };
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.Human, key.Source);
    }

    [Fact]
    public void BotBelowRiskBandFloor_FallsThroughToHuman()
    {
        var grouper = Build();
        var input = Bot("sig:x", "Amazonbot", "Scraper") with { RiskBand = "VeryLow" };
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.Human, key.Source);
    }

    [Fact]
    public void IsBotFalse_ForcesHumanRegardlessOfOtherSignals()
    {
        var grouper = Build();
        var input = Bot("sig:x", "Googlebot", "VerifiedBot") with { IsBot = false };
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.Human, key.Source);
    }

    // ============================================================
    //  Tier 1: Identity (pre-populated FingerprintId)
    // ============================================================

    [Fact]
    public void IdentityTier_FingerprintIdProduced_WinsOverEverythingBelow()
    {
        var grouper = Build();
        var input = Bot("sig:x", "Amazonbot", "Scraper") with { FingerprintId = "fp_abc123" };
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.Identity, key.Source);
        Assert.Equal("fp_abc123", key.Value);
    }

    [Fact]
    public void IdentityTier_Disabled_FallsThrough()
    {
        var grouper = Build(new GroupingOptions { EnableIdentityTier = false });
        var input = Bot("sig:x", "Amazonbot", "Scraper") with { FingerprintId = "fp_abc" };
        var key = grouper.Resolve(input);

        // Falls through to FriendlyBotName (Amazonbot+Scraper passes).
        Assert.Equal(GroupKeySource.FriendlyBotName, key.Source);
    }

    // ============================================================
    //  Tier 2: Cluster
    // ============================================================

    [Fact]
    public void ClusterTier_ClusterIdProduced_WinsOverFriendlyName()
    {
        var grouper = Build();
        var input = Bot("sig:x", "Amazonbot", "Scraper") with { ClusterId = "c47" };
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.Cluster, key.Source);
        Assert.Equal("c47", key.Value);
    }

    [Fact]
    public void IdentityBeatsCluster()
    {
        var grouper = Build();
        var input = Bot("sig:x", "Amazonbot", "Scraper") with
        {
            FingerprintId = "fp_xyz",
            ClusterId = "c47"
        };
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.Identity, key.Source);
    }

    // ============================================================
    //  Tier 6: Friendly bot name (the current dashboard rule)
    // ============================================================

    [Fact]
    public void FriendlyName_AmazonbotPlusAmazonbot_GroupSameKey()
    {
        var grouper = Build();
        var keyA = grouper.Resolve(Bot("sig:a", "Amazonbot", "Scraper"));
        var keyB = grouper.Resolve(Bot("sig:b", "Amazonbot", "Scraper"));

        Assert.Equal(keyA.Canonical, keyB.Canonical);
        Assert.Equal(GroupKeySource.FriendlyBotName, keyA.Source);
    }

    [Fact]
    public void FriendlyName_ToolBotType_DoesNotGroup()
    {
        // Two tools sharing a UA family are different actors; current rule.
        var grouper = Build();
        var a = Bot("sig:a", "curl", "Tool");
        var b = Bot("sig:b", "curl", "Tool");

        var keyA = grouper.Resolve(a);
        var keyB = grouper.Resolve(b);

        // Friendly tier is gated off for "Tool" -> falls through to raw.
        Assert.Equal(GroupKeySource.RawSignature, keyA.Source);
        Assert.NotEqual(keyA.Canonical, keyB.Canonical);
    }

    [Fact]
    public void FriendlyName_UnknownBotType_DoesNotGroup()
    {
        var grouper = Build();
        var a = Bot("sig:a", "Mystery", "Unknown");
        var b = Bot("sig:b", "Mystery", "Unknown");
        var keyA = grouper.Resolve(a);
        var keyB = grouper.Resolve(b);

        Assert.Equal(GroupKeySource.RawSignature, keyA.Source);
        Assert.NotEqual(keyA.Canonical, keyB.Canonical);
    }

    [Fact]
    public void CustomBotName_AlwaysWinsAsFriendlyName()
    {
        var grouper = Build();
        var input = Bot("sig:x") with { CustomBotName = "operator-pinned" };
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.FriendlyBotName, key.Source);
        Assert.Equal("operator-pinned", key.Value);
    }

    // ============================================================
    //  Tier 7: Raw signature fallback
    // ============================================================

    [Fact]
    public void NoSignalsAvailable_FallsThroughToRawSignature()
    {
        var grouper = Build();
        // Bot probability passes the gate but no name / cluster / fp / etc.
        var input = Bot("sig:nameless");
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.RawSignature, key.Source);
        Assert.Equal("sig:nameless", key.Value);
    }

    // ============================================================
    //  Master switch
    // ============================================================

    [Fact]
    public void Disabled_AllRequestsReturnRawSignature()
    {
        var grouper = Build(new GroupingOptions { Enabled = false });
        var input = Bot("sig:a", "Amazonbot", "Scraper") with
        {
            FingerprintId = "fp_x",
            ClusterId = "c1"
        };
        var key = grouper.Resolve(input);

        Assert.Equal(GroupKeySource.RawSignature, key.Source);
    }

    private sealed class TestMonitor<T>(T value) : IOptions<T> where T : class
    {
        public T Value { get; } = value;
    }
}
